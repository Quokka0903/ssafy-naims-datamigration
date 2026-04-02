using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DBImplant_N1toN2.Audio.Writer;

namespace DBImplant_N1toN2.Audio
{



    // [1] fmt chunk를 표현하는 구조체 - 오디오 포맷 정보 저장
    [StructLayout(LayoutKind.Explicit, Size = 18)]
    public struct WaveFormat
    {
        [FieldOffset(0)] public ushort FormatTag;        // 오디오 포맷 (1 = PCM)
        [FieldOffset(2)] public ushort Channels;         // 채널 수 (1 = 모노, 2 = 스테레오)
        [FieldOffset(4)] public uint SamplesPerSec;      // 초당 샘플 수
        [FieldOffset(8)] public uint AvgBytesPerSec;     // 초당 평균 바이트 수
        [FieldOffset(12)] public ushort BlockAlign;       // 채널 * 샘플당 바이트 수
        [FieldOffset(14)] public ushort BitsPerSample;    // 비트 깊이 (16 등)
        [FieldOffset(16)] public ushort ExtraSize;        // 확장 필드 (없으면 0)
    }

    // [2] 방송 메타데이터 (bext chunk)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public class BextChunk
    {
        public BextChunk()
        {
            UMID = new byte[64];
            Reserved = new byte[190];
        }

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public string Description;           // 설명

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public string Originator;            // 생성자 (기관 등)

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public string OriginatorReference;   // 생성자 참조 ID

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 11)]
        public string OriginationDate;       // 생성 날짜 yyyy-mm-dd

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
        public string OriginationTime;       // 생성 시간 hh-mm-ss

        public uint TimeReferenceLow;
        public uint TimeReferenceHigh;                         // 오디오 시간 기준 위치

        public ushort Version = 1;                          // BWF 버전 (보통 1)

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] UMID;                 // 고유 식별자 UMID

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 190)]
        public byte[] Reserved;            // 예약 필드 (0으로 패딩)

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public string CodingHistory;        // 변환 이력 등 기록
    }


    // [3] 추가 메타데이터 청크 (fact chunk) - 벤더 전용 정보 저장
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public class FactChunk
    {
        public Int32 StartTime;          // CTime (년월일 시분초)             4byte
        public Int32 StartUsecTime;      // 초미만값을 micro sec로 환산값.     4byte
        public Int32 EndTime;            // CTime (년월일 시분초)	             4byte
        public Int32 EndUsecTime;        // 초미만값을 micro sec로 환산값.     4byte

        public ushort BuoyNorthDeg;     // ASCII : 위도 도.
        public ushort BuoyNorthMin;     // ASCII : 위도 분.
        public ushort BuoyNorthSec;     // ASCII : 위도 초.

        public ushort BuoyEastDeg;      // ASCII : 경도 도.
        public ushort BuoyEastMin;      // ASCII : 경도 분.
        public ushort BuoyEastSec;      // ASCII : 경도 초.

        public float CenterFreq;        // center frequency
        public float SaveSampleRate;    // sampling rate

        public float FamsGain;          // FAMS gain
        public float FamsCpaDistance;   // FAMS CPA 거리.
        public float FamsCpaTime;       // FAMS CPA 시간.

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 81)]
        public byte[] Comment;

        public byte InputRange;         // 0:default 1Vpp, 1:+/-2.5Vpp 2:+/-5Vpp 3:+/-10Vpp

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 382)]
        public byte[] Reserved1;        // 필드 추가시 변경됨.


        // 유틸리티
        public string GetComment() => Encoding.ASCII.GetString(Comment).TrimEnd('\0');
        public void SetComment(string text)
        {
            Comment = new byte[81];
            var bytes = Encoding.ASCII.GetBytes(text ?? "");
            Array.Copy(bytes, Comment, Math.Min(bytes.Length, 81));
        }
    }

    internal class BwfFileInfo
    {
        public BwfHeader miBWfHeader;
        public Int32 mn32HeaderSize;
        public UInt32 mu32SigDataCnt;
        public byte[] pcmData = null;
        public double[] mdSigData = null;

        internal static BwfFileInfo ReadBwfFileInfo(string path)
        {
            BwfFileInfo iBwfFileInfo = null;

            // 파일이 존재하지 않으면 null 반환
            if (File.Exists(path))
            {
                byte[] data = File.ReadAllBytes(path);

                iBwfFileInfo = ReadBwfFileInfoFromByte(data);
            }

            return iBwfFileInfo;
        }

        internal static BwfFileInfo ReadBwfFileInfoFromByte(byte[] data)
        {
            var info = new BwfFileInfo();

            using (var ms = new MemoryStream(data, writable: false))
            using (var br = new BinaryReader(ms, Encoding.ASCII, leaveOpen: false))
            {
                var header = new BwfHeader();
                info.mn32HeaderSize = 0;

                // RIFF / WAVE
                header.RiffID = ReadFourCC(br);            // "RIFF"
                info.mn32HeaderSize += 4;

                header.FileSize = br.ReadUInt32();      // fileSize = total-8 (관례)
                info.mn32HeaderSize += 4;

                header.WaveID = ReadFourCC(br);            // "WAVE"
                info.mn32HeaderSize += 4;

                long expectedSize = header.FileSize + 8L;
                // 과소 신고(파일이 실제보다 작다)는 치명적, 과다 신고/불일치는 허용
                if (ms.Length < expectedSize)
                    throw new InvalidDataException($"File length smaller than RIFF size. Header: {expectedSize}, Actual: {ms.Length}");

                // 청크 루프
                while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
                {
                    long headerPos = br.BaseStream.Position;

                    string chunkID = ReadFourCC(br);
                    info.mn32HeaderSize += 4;

                    uint declSize = br.ReadUInt32();
                    info.mn32HeaderSize += 4;

                    // 크기 비합리 → 재동기화 시도
                    if (br.BaseStream.Position + declSize > br.BaseStream.Length)
                    {
                        // 관용 모드: 근처에서 재동기화 시도
                        if (TryResync(br, out chunkID, out declSize))
                        {
                            // 재동기화 성공 → 다시 헤더 소비
                            chunkID = ReadFourCC(br);
                            info.mn32HeaderSize += 4;
                            declSize = br.ReadUInt32();
                            info.mn32HeaderSize += 4;
                        }
                        else
                        {
                            // 재동기화 실패: 남은 영역 무시하고 종료
                            break;
                        }
                    }

                    switch (chunkID)
                    {
                        case "bext":
                            {
                                header.BextChunkSize = declSize;
                                byte[] bext = ReadExact(br, (int)declSize);
                                info.mn32HeaderSize += (int)declSize;

                                header.Bext.Description = Encoding.ASCII.GetString(bext, 0, 256).TrimEnd('\0');
                                header.Bext.Originator = Encoding.ASCII.GetString(bext, 256, 32).TrimEnd('\0');
                                header.Bext.OriginatorReference = Encoding.ASCII.GetString(bext, 288, 32).TrimEnd('\0');
                                header.Bext.OriginationDate = Encoding.ASCII.GetString(bext, 320, 10).TrimEnd('\0');
                                header.Bext.OriginationTime = Encoding.ASCII.GetString(bext, 330, 8).TrimEnd('\0');
                                header.Bext.TimeReferenceHigh = BitConverter.ToUInt32(bext, 338);
                                header.Bext.TimeReferenceLow = BitConverter.ToUInt32(bext, 342);
                                header.Bext.Version = BitConverter.ToUInt16(bext, 346);
                                Array.Copy(bext, 348, header.Bext.UMID, 0, 64);
                                Array.Copy(bext, 412, header.Bext.Reserved, 0, 190);

                                const int codingHistoryOffset = 602;
                                if (declSize > codingHistoryOffset)
                                {
                                    int chLen = (int)declSize - codingHistoryOffset;
                                    header.Bext.CodingHistory = Encoding.ASCII
                                        .GetString(bext, codingHistoryOffset, chLen)
                                        .TrimEnd('\0', '\r', '\n');
                                }
                                else header.Bext.CodingHistory = string.Empty;

                                SkipPadIfOdd(br, declSize);
                                break;
                            }

                        case "fmt ":
                            {
                                header.FmtID = chunkID;
                                header.FmtSize = declSize;

                                byte[] fmt = ReadExact(br, (int)declSize);
                                info.mn32HeaderSize += (int)declSize;

                                IntPtr p = Marshal.AllocHGlobal(fmt.Length);
                                try
                                {
                                    Marshal.Copy(fmt, 0, p, fmt.Length);
                                    header.Format = (WaveFormat)Marshal.PtrToStructure(p, typeof(WaveFormat));
                                }
                                finally { Marshal.FreeHGlobal(p); }

                                SkipPadIfOdd(br, declSize);
                                break;
                            }

                        case "NDID":
                            {
                                header.FactID = chunkID;
                                header.FactChunkSize = declSize;

                                header.FactRaw = ReadExact(br, (int)declSize);
                                info.mn32HeaderSize += (int)declSize;

                                header.FactChunk = BwfHeader.ByteArrayToFactChunk(header.FactRaw);

                                SkipPadIfOdd(br, declSize);
                                break;
                            }

                        case "TFID":
                            {
                                header.FactID = chunkID;
                                header.FactChunkSize = declSize;

                                byte[] tfidRaw = ReadExact(br, (int)declSize);
                                info.mn32HeaderSize += (int)declSize;

                                header.FactRaw = tfidRaw;
                                header.FactChunk = new FactChunk
                                {
                                    StartTime = FactChunkParser.ParseTFIDStartTime(tfidRaw)
                                };

                                SkipPadIfOdd(br, declSize);
                                break;
                            }

                        case "data":
                            {
                                header.DataID = chunkID;
                                header.DataSize = declSize;

                                byte[] dataBytes = ReadExact(br, (int)declSize);
                                info.mn32HeaderSize += (int)declSize;
                                SkipPadIfOdd(br, declSize);

                                // 16-bit PCM(모노 가정: 기존 로직 유지)
                                info.mu32SigDataCnt = (uint)(header.DataSize / 2);
                                var n16 = new short[info.mu32SigDataCnt];
                                Buffer.BlockCopy(dataBytes, 0, n16, 0, dataBytes.Length);

                                info.mdSigData = new double[info.mu32SigDataCnt];

                                double dRange;
                                switch (header.FactChunk.InputRange)
                                {
                                    case 0: dRange = 1.0; break;
                                    case 1: dRange = 2.5; break;
                                    case 2: dRange = 5.0; break;
                                    case 3: dRange = 10.0; break;
                                    default: dRange = 1.0; break;
                                }

                                for (int i = 0; i < info.mu32SigDataCnt; i++)
                                    info.mdSigData[i] = n16[i] / 32768.0 * dRange;

                                header.FactChunk.EndTime =
                                    header.FactChunk.StartTime + (int)(header.DataSize / header.Format.AvgBytesPerSec);

                                break;
                            }

                        default:
                            {
                                // 여기 들어왔다는 건 chunkID가 s_knownIds에 없다는 뜻(=헤더가 어긋났을 가능성 높음).
                                // 바로 스킵하지 말고, 헤더 위치 주변을 ±2바이트 진동하며 재동기화 시도.
                                // headerPos는 "이번 청크를 읽기 직전 헤더의 시작 위치"여야 한다.
                                // → while 루프 시작부에서 long headerPos = br.BaseStream.Position; 를 잡아둔 상태여야 함.
                                //    (이미 그렇게 되어 있다면 그대로 사용)

                                // ① 우선 짧은 ±2바이트 지터 재동기화
                                if (TryJitterResyncAround(br, headerPos, 2, out var _newId, out var _newSize))
                                {
                                    // 잘못 더한 헤더 8바이트 롤백
                                    if (info.mn32HeaderSize >= 8) info.mn32HeaderSize -= 8;

                                    // 이제 루프 처음으로 돌아가서 새 헤더를 정상적으로 다시 읽는다
                                    continue;
                                }

                                // ② 실패 시, 주변 소범위 스캔(예: +64바이트)으로 헤더 재발견 시도
                                if (TryShortScanResync(br, headerPos, 64, out _newId, out _newSize))
                                {
                                    if (info.mn32HeaderSize >= 8) info.mn32HeaderSize -= 8;
                                    continue;
                                }

                                // ③ 그래도 실패하면, 원래 로직대로 'declSize'만큼 건너뛰고 패딩 처리
                                br.BaseStream.Seek(declSize, SeekOrigin.Current);
                                info.mn32HeaderSize += (int)declSize;
                                SkipPadIfOdd(br, declSize);
                                break;
                            }

                    }
                }

                info.miBWfHeader = header;
                return info;
            }
        }


        private static void SkipPadIfOdd(BinaryReader br, uint declSize)
        {
            if ((declSize & 1u) != 0)
                br.BaseStream.Seek(1, SeekOrigin.Current);
        }

        private static string ReadFourCC(BinaryReader br)
        {
            byte[] id = br.ReadBytes(4);
            if (id.Length != 4) throw new InvalidDataException("Unexpected EOF while reading chunk id.");
            return Encoding.ASCII.GetString(id);
        }

        private static byte[] ReadExact(BinaryReader br, int count)
        {
            byte[] buf = br.ReadBytes(count);
            if (buf.Length != count) throw new InvalidDataException("Unexpected EOF while reading chunk payload.");
            return buf;
        }

        // 알려진(혹은 허용) FourCC – 필요 시 추가
        private static readonly System.Collections.Generic.HashSet<string> s_knownIds =
            new System.Collections.Generic.HashSet<string>
        {
    "RIFF","WAVE","bext","fmt ","data","fact","LIST","JUNK","PAD ",
    "NDID","TFID","iXML","cue ","smpl","plst","minf","elm1","levl","link","axml"
        };

        // 4바이트가 인쇄 가능한 ASCII(공백 포함)인지 검사
        private static bool IsPrintableFourCC(byte[] id4)
        {
            if (id4 == null || id4.Length != 4) return false;
            for (int i = 0; i < 4; i++)
            {
                byte b = id4[i];
                if (b < 0x20 || b > 0x7E) return false;  // ' ' ~ '~'
            }
            return true;
        }

        // 특정 위치에서 헤더(id+size) 유효성 검사 (최소한의 휴리스틱 포함)
        private static bool IsPlausibleHeader(Stream s, long pos, out string id, out uint size)
        {
            id = null; size = 0;

            if (pos < 0 || pos + 8 > s.Length) return false;

            long save = s.Position;
            try
            {
                s.Position = pos;
                var br = new System.IO.BinaryReader(s, Encoding.ASCII, leaveOpen: true);

                byte[] idBytes = br.ReadBytes(4);
                if (!IsPrintableFourCC(idBytes)) return false;

                id = Encoding.ASCII.GetString(idBytes);
                size = br.ReadUInt32();

                // 페이로드가 스트림 범위를 벗어나면 불가
                if (pos + 8 + size > s.Length) return false;

                // 알려진 FourCC는 우선 가산점
                if (s_knownIds.Contains(id))
                {
                    // 일부 기본 휴리스틱(오탐 줄이기)
                    if (id == "fmt " && size < 16) return false;   // 최소 16바이트(PCM)
                    if (id == "data" && size == 0) return false;   // 0 데이터는 의미 없음
                                                                   // bext는 가변이나 보통 256+ 영역, 최소 문턱 조금만 둔다
                    if (id == "bext" && size < 256) return false;
                    return true;
                }

                // 모르는 FourCC라도 인쇄가능/범위합리 → 일단 허용
                return true;
            }
            finally
            {
                s.Position = save;
            }
        }

        // 헤더 주변을 ±jitter 바이트 범위에서 재동기화(짧은 탐색)
        // 예: jitter=2 → headerPos-2, -1, +1, +2 네 군데 검사
        private static bool TryJitterResyncAround(System.IO.BinaryReader br, long headerPos,
                                                  int jitter, out string id, out uint size)
        {
            id = null; size = 0;
            var s = br.BaseStream;

            for (int d = 1; d <= jitter; d++)
            {
                long back = headerPos - d;
                long fwd = headerPos + d;

                if (IsPlausibleHeader(s, back, out id, out size))
                {
                    s.Position = back;
                    return true;
                }
                if (IsPlausibleHeader(s, fwd, out id, out size))
                {
                    s.Position = fwd;
                    return true;
                }
            }
            return false;
        }

        // 짧은 범위 슬라이딩 스캔(옵션): 주변 N바이트에서 합리적 헤더 찾기
        private static bool TryShortScanResync(System.IO.BinaryReader br, long headerPos,
                                               int scanBytes, out string id, out uint size)
        {
            id = null; size = 0;
            var s = br.BaseStream;

            long begin = System.Math.Max(0, headerPos - 8);
            long end = System.Math.Min(s.Length, headerPos + scanBytes);

            for (long p = begin; p + 8 <= end; p++)
            {
                if (IsPlausibleHeader(s, p, out id, out size))
                {
                    s.Position = p;
                    return true;
                }
            }
            return false;
        }


        // 관용 파서용: 근처에서 합리적인 FourCC와 길이를 찾아 재동기화
        private static bool TryResync(BinaryReader br, out string id, out uint size, int scanBytes = 4096)
        {
            long start = br.BaseStream.Position;
            long begin = Math.Max(0, start - 3);                        // 헤더가 반쯤 밀렸을 가능성 대비
            long limit = Math.Min(br.BaseStream.Length, start + scanBytes);

            for (long p = begin; p + 8 <= limit; p++)
            {
                br.BaseStream.Position = p;
                string candId = ReadFourCC(br);
                uint candSize = br.ReadUInt32();

                // 후보 ID가 알려진 것이어야 하고, 크기가 합리적이어야 함
                if (s_knownIds.Contains(candId) &&
                    (br.BaseStream.Position + candSize) <= br.BaseStream.Length)
                {
                    br.BaseStream.Position = p; // 성공: 여기부터 다시 읽기
                    id = candId; size = candSize;
                    return true;
                }
            }
            // 실패: 원위치로 복귀
            br.BaseStream.Position = start;
            id = null; size = 0;
            return false;
        }
    }





    // [4] 전체 BWF 헤더 클래스 정의 - 각 청크와 필드를 하나로 구성
    public partial class BwfHeader
    {
        public string RiffID = "RIFF";     // 파일 헤더 ID
        public uint FileSize;              // 전체 파일 크기 (bext~data 포함)
        public string WaveID = "WAVE";     // WAVE 형식 지정 ID

        public string BextID = "bext";     // 방송 메타데이터 청크 ID
        public uint BextChunkSize = 858;    // bext 크기 (기본값 858, C++ 참고)
        public BextChunk Bext = new BextChunk(); // bext 필드 집합

        public string FmtID = "fmt ";      // 포맷 청크 ID
        public uint FmtSize = 18;           // fmt 청크 크기
        public WaveFormat Format;          // WaveFormat 구조체

        public string FactID = "NDID";      // fact 청크 ID (vendor specific)
        public uint FactChunkSize = 512;    // fact 청크 크기
        public byte[] FactRaw = new byte[512]; // fact chunk 원본 그대로 저장

        public string DataID = "data";     // PCM 데이터 식별자
        public uint DataSize;              // 실제 음성 데이터 바이트 수


        // FactChunk - FactRaw를 byte[]로 변환하여 저장
        private FactChunk _factChunk;
        public FactChunk FactChunk
        {
            get
            {
                if (_factChunk == null && FactRaw?.Length >= 512) //기본값 512, FactRaw가 그것보다 더 크면 해당 길이로
                    _factChunk = ByteArrayToFactChunk(FactRaw);
                return _factChunk;
            }
            set
            {
                _factChunk = value;
                FactRaw = FactChunkToByteArray(value);
            }
        }

        public static FactChunk ByteArrayToFactChunk(byte[] raw) // byte[]로 변환
        {
            if (raw == null || raw.Length < 512)
                return new FactChunk();

            var handle = GCHandle.Alloc(raw, GCHandleType.Pinned);
            try
            {
                return (FactChunk)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(FactChunk));
            }
            finally
            {
                handle.Free();
            }
        }

        public static byte[] FactChunkToByteArray(FactChunk chunk)
        {
            var size = Marshal.SizeOf(typeof(FactChunk));
            var raw = new byte[size];
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(chunk, ptr, true);
                Marshal.Copy(ptr, raw, 0, size);
                return raw;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }



    // [5] Vendor-specific 청크 해석기 - 현재는 TFID 파싱 기능 제공
    public static class FactChunkParser
    {
        // TFID 청크에서 시간 문자열을 추출하여 UTC timestamp로 변환
        public static int ParseTFIDStartTime(byte[] data)
        {
            // 26바이트 이상이 아니면 TFID 날짜 데이터가 없음
            if (data == null || data.Length < 26)
                return 0;

            try
            {
                // 연, 월, 일, 시, 분, 초를 ASCII 숫자로 파싱
                int year = (data[8] - '0') * 1000 + (data[9] - '0') * 100 + (data[10] - '0') * 10 + (data[11] - '0');
                int month = (data[13] - '0') * 10 + (data[14] - '0');
                int day = (data[16] - '0') * 10 + (data[17] - '0');
                int hour = (data[18] - '0') * 10 + (data[19] - '0');
                int min = (data[21] - '0') * 10 + (data[22] - '0');
                int sec = (data[24] - '0') * 10 + (data[25] - '0');

                // 유효한 날짜/시간 범위인지 검증
                if (year >= 1900 && year <= 3000 && month >= 1 && month <= 12 && day >= 1 && day <= 31 &&
                    hour >= 0 && hour <= 23 && min >= 0 && min <= 59 && sec >= 0 && sec <= 59)
                {
                    var dt = new DateTime(year, month, day, hour, min, sec, DateTimeKind.Utc);
                    return (int)(dt - new DateTime(1970, 1, 1)).TotalSeconds; // Unix timestamp 반환
                }
            }
            catch { }

            return 0; // 실패 시 0 반환
        }
    }


    // [5] BWF 파일 읽기/쓰기 클래스 (BinaryReader/Writer 기반)
    public static class BwfFile
    {
        // 문자열을 고정 길이의 ASCII byte 배열로 변환하여 리턴하는 함수
        private static byte[] ToFixedAscii(string input, int length)
        {
            var bytes = Encoding.ASCII.GetBytes(input ?? "");      // 입력 문자열을 ASCII 인코딩하여 byte 배열로 변환
            Array.Resize(ref bytes, length);                       // 지정된 길이에 맞춰 배열 크기 조정 (남는 부분은 0으로 패딩됨)
            return bytes;                                          // 변환된 배열 반환
        }

        // ========================
        // BWF 파일 쓰기 함수
        // ========================
        /*public static void Write(string path, BwfHeader header, byte[] pcmData)
        {
            header.DataSize = (uint)(pcmData != null ? pcmData.Length : 0);           // PCM 데이터의 크기를 설정 (null이면 0)

            *//*
            수치	                    의미	                    상세 설명

            4           	        "WAVE"      	        WAVE 포맷 ID (4바이트)

            4           	        "bext"      	        bext 청크 ID
            4           	        BextChunkSize	        bext 청크 크기 (크기 필드)
            header.BextChunkSize	bext 내용 크기	        바로 위에 적힌 만큼의 크기

            4	                    "fmt "              	fmt 청크 ID
            4                   	FmtSize             	fmt 크기 필드 (보통 18)
            18                  	fmt                      데이터 실제 길이	WaveFormat 구조체 크기

            4                   	"fact"/"NDID"       	fact ID (FTID는 쓰기 기능 없음)
            4                   	FactChunkSize       	fact 크기 필드
            header.FactChunkSize	fact 내용 크기	        바로 위에 적힌 만큼의 NDID 청크 크기

            4                   	"data"              	데이터 청크 ID
            4	                    DataSize            	PCM 데이터의 바이트 수
            header.DataSize	        PCM 실제 데이터 크기	    PCM 데이터 내용
             *//*

            // header.FileSize = 실제 합산값
            header.FileSize = 1424 + (uint)(pcmData != null ? pcmData.Length : 0);



            Debug.WriteLine($"[FileSize 계산] header.FileSize = {header.FileSize}");


            header.Format.AvgBytesPerSec = header.Format.SamplesPerSec * header.Format.Channels * header.Format.BitsPerSample / 8; // 평균 바이트 계산

            // FactChunk → FactRaw 변환
            if (header.FactChunk != null)
                header.FactRaw = BwfHeader.FactChunkToByteArray(header.FactChunk);


            using (var fs = new FileStream(path, FileMode.Create))                   // 새 파일을 생성하고 FileStream 열기
            using (var bw = new BinaryWriter(fs, Encoding.ASCII))                    // BinaryWriter로 ASCII 형식의 파일 쓰기
            {

                void TracePos(string label)
                {
                    Debug.WriteLine($"{label,-10} 위치 = {fs.Position} (0x{fs.Position:X})");
                }

                bw.Write(Encoding.ASCII.GetBytes(header.RiffID));                    // "RIFF" 쓰기
                bw.Write(header.FileSize);                                           // 파일 전체 크기 쓰기
                bw.Write(Encoding.ASCII.GetBytes(header.WaveID));                    // "WAVE" 쓰기
                TracePos("[RIFF]");

                // bext 쓰기
                TracePos("[bext 시작]");
                bw.Write(Encoding.ASCII.GetBytes(header.BextID));                    // "bext" 쓰기
                bw.Write(header.BextChunkSize);                                      // bext 크기 쓰기
                bw.Write(ToFixedAscii(header.Bext.Description, 256));                // 설명
                bw.Write(ToFixedAscii(header.Bext.Originator, 32));                  // 생성자
                bw.Write(ToFixedAscii(header.Bext.OriginatorReference, 32));         // 참조
                bw.Write(ToFixedAscii(header.Bext.OriginationDate, 10));             // 날짜
                bw.Write(ToFixedAscii(header.Bext.OriginationTime, 8));              // 시간
                bw.Write((uint)(header.Bext.TimeReferenceHigh));            // 참조 시간 하위 32비트
                bw.Write((uint)(header.Bext.TimeReferenceLow));                   // 참조 시간 상위 32비트
                bw.Write(header.Bext.Version);                                       // 버전
                bw.Write(header.Bext.UMID);                                          // UMID 쓰기
                bw.Write(header.Bext.Reserved);                                      // 예약 공간
                bw.Write(ToFixedAscii(header.Bext.CodingHistory, 256));              // 코딩 히스토리
                TracePos("[bext 끝]");



                // fmt 쓰기
                TracePos("[fmt 시작]");
                bw.Write(Encoding.ASCII.GetBytes(header.FmtID));                     // "fmt " 쓰기
                bw.Write(header.FmtSize);                                            // fmt chunk 크기 쓰기

                int size = Marshal.SizeOf(typeof(WaveFormat));                       // 구조체 크기 계산
                var fmtData = new byte[size];                                        // 포맷 데이터를 담을 배열
                var ptr = Marshal.AllocHGlobal(size);                                // 비관리 메모리 할당
                Marshal.StructureToPtr(header.Format, ptr, true);                    // 구조체를 포인터로 복사
                Marshal.Copy(ptr, fmtData, 0, size);                                 // 포인터 데이터를 배열로 복사
                Marshal.FreeHGlobal(ptr);                                            // 메모리 해제
                bw.Write(fmtData);                                                   // 포맷 데이터를 파일에 쓰기
                TracePos("[fmt 끝]");

                // fact 쓰기 (C++ 코드와 동일하게 NDID에 대한 쓰기만 하는 것으로 기대)
                // 먼저 FactRaw를 FactChunkSize에 맞춰 자름
                TracePos("[fact 시작]");
                if (header.FactRaw.Length > header.FactChunkSize)
                    header.FactRaw = header.FactRaw.Take((int)header.FactChunkSize).ToArray();
                bw.Write(Encoding.ASCII.GetBytes(header.FactID));                    // "NDID" 또는 다른 fact ID 쓰기
                bw.Write(header.FactChunkSize);                                      // fact chunk 크기 쓰기
                bw.Write(header.FactRaw.Take((int)header.FactChunkSize).ToArray());  // fact chunk 데이터 쓰기
                TracePos("[fact 끝]");

                TracePos("[data 시작]");
                bw.Write(Encoding.ASCII.GetBytes(header.DataID));                    // "data" 쓰기
                bw.Write(header.DataSize);                                           // PCM 데이터 크기 쓰기
                if (pcmData != null)                                                 // PCM 데이터가 있다면
                    bw.Write(pcmData);                                               // PCM 데이터 쓰기
                TracePos("[data 끝]");


                Debug.WriteLine($"[data] 실제 쓴 byte 수 = {header.DataSize}");
                Debug.WriteLine($"[data] 위치 = {fs.Position} (0x{fs.Position:X})");
            }

            // 파일 크기 검증 (디버깅 목적)
            long actualSize = new FileInfo(path).Length;
            long expectedSize = header.FileSize + 8; // RIFF ID(4) + FileSize(4)
            Debug.WriteLine($"[최종검증] Header: {expectedSize}, Actual: {actualSize}");

            if (actualSize != expectedSize)
                throw new InvalidDataException($"File size mismatch after write. Header: {expectedSize}, Actual: {actualSize}");
        }*/

        // 헤더/데이터를 받아 BWF(WAV)를 기록하는 메서드 (패딩/순서/크기 동기화/자가검증 모두 반영)
        public static void Write(string path, BwfHeader header, byte[] pcmData)
        {
            // 1) 방어적 검증: 인자 널 체크
            if (header == null) throw new ArgumentNullException(nameof(header));
            if (header.Bext == null) header.Bext = new BextChunk();

            // 2) 로컬 유틸: 고정 길이 ASCII로 채우기(남으면 0x00 패딩, 넘치면 잘라냄)
            byte[] ToFixedAscii(string s, int len)
            {
                // 널은 빈 문자열로 간주
                if (s == null) s = string.Empty;
                // ASCII 인코딩
                var bytes = Encoding.ASCII.GetBytes(s);
                // 대상 버퍼
                var buf = new byte[len];
                // 복사 길이 계산
                int n = Math.Min(len, bytes.Length);
                // 복사
                Buffer.BlockCopy(bytes, 0, buf, 0, n);
                // 남는 부분은 0으로 유지
                return buf;
            }

            // 3) 로컬 유틸: 청크 크기가 홀수이면 1바이트 패딩(0x00) 쓰기
            void WritePadIfOdd(BinaryWriter bw, uint size, string label)
            {
                // 홀수 검사
                if ((size & 1u) == 1u)
                {
                    // 패딩 바이트 기록
                    bw.Write((byte)0);
                    // 위치 로그
                    Debug.WriteLine($"[{label}] 패딩 1바이트 추가 → 위치 = {((FileStream)bw.BaseStream).Position} (0x{((FileStream)bw.BaseStream).Position:X})");
                }
            }

            // 4) BEXT 필드의 필수 배열(UMID/Reserved) 널 방지 및 길이 보정
            if (header.Bext.UMID == null || header.Bext.UMID.Length != 64)
            {
                // 64바이트 UMID 보정
                header.Bext.UMID = (header.Bext.UMID ?? new byte[0]).Take(64).Concat(new byte[64]).Take(64).ToArray();
            }
            if (header.Bext.Reserved == null || header.Bext.Reserved.Length != 190)
            {
                // 190바이트 Reserved 보정
                header.Bext.Reserved = (header.Bext.Reserved ?? new byte[0]).Take(190).Concat(new byte[190]).Take(190).ToArray();
            }

            // 5) 실제로 쓸 bext 페이로드 길이 계산(아래와 정확히 동일하게 써야 함)
            //    Description(256) + Originator(32) + OriginatorReference(32) + Date(10) + Time(8)
            //    + TimeReference Low(4) + High(4) + Version(2) + UMID(64) + Reserved(190) + CodingHistory(256)
            uint bextPayloadSize =
                256u + 32u + 32u + 10u + 8u + 4u + 4u + 2u + 64u + 190u + 256u;

            // 6) bext 청크 크기 동기화
            header.BextChunkSize = bextPayloadSize;

            // 7) fact 원시 데이터 준비(널 허용 → 빈 배열)
            if (header.FactChunk != null && (header.FactRaw == null || header.FactRaw.Length == 0))
            {
                // NDID 등 구조체가 있다면 Raw로 직렬화
                header.FactRaw = BwfHeader.FactChunkToByteArray(header.FactChunk);
            }
            if (header.FactRaw == null) header.FactRaw = new byte[0];

            // 8) fact 쓰기 길이 결정: FactChunkSize가 0이면 Raw 길이로 동기화
            if (header.FactChunkSize == 0)
            {
                // Raw 길이를 크기 필드에 반영
                header.FactChunkSize = (uint)header.FactRaw.Length;
            }

            // 9) PCM 길이 계산 및 DataSize 동기화
            uint dataSize = (uint)((pcmData != null) ? pcmData.Length : 0);
            header.DataSize = dataSize;

            // 10) 포맷(fmt) 바이트 만들기(실제 길이 동기화 목적)
            int fmtStructSize = Marshal.SizeOf(typeof(WaveFormat));
            byte[] fmtData = new byte[fmtStructSize];
            IntPtr ptr = Marshal.AllocHGlobal(fmtStructSize);
            try
            {
                // 구조체 → 포인터
                Marshal.StructureToPtr(header.Format, ptr, true);
                // 포인터 → 바이트
                Marshal.Copy(ptr, fmtData, 0, fmtStructSize);
            }
            finally
            {
                // 포인터 해제
                Marshal.FreeHGlobal(ptr);
            }

            // 11) fmt 청크 크기 동기화(중요: 실제 쓰는 길이와 일치시킴)
            header.FmtSize = (uint)fmtData.Length;

            // 12) AvgBytesPerSec 재계산(포맷 정보가 맞지 않으면 지속적으로 어긋남)
            header.Format.AvgBytesPerSec =
                header.Format.SamplesPerSec * header.Format.Channels * header.Format.BitsPerSample / 8;

            // 13) 파일 스트림/라이터 열기 (ReadWrite로 열어 FileSize 재기입 가능하게)
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var bw = new BinaryWriter(fs, Encoding.ASCII))
            {
                // 13-1) 위치 트레이스 로컬 함수
                void TracePos(string label)
                {
                    // 현재 파일 포인터 위치 로깅
                    Debug.WriteLine($"{label,-12} 위치 = {fs.Position} (0x{fs.Position:X})");
                }

                // 14) RIFF 헤더: "RIFF" + FileSize(placeholder) + "WAVE"
                bw.Write(Encoding.ASCII.GetBytes(header.RiffID ?? "RIFF"));   // "RIFF" 또는 "RF64" 등
                long riffSizePos = fs.Position;                               // FileSize 필드 오프셋 기억(4바이트)
                bw.Write(0u);                                                  // 임시 0(나중에 최종 길이로 덮어씀)
                bw.Write(Encoding.ASCII.GetBytes(header.WaveID ?? "WAVE"));   // "WAVE"
                TracePos("[RIFF]");

                // 15) bext 청크 쓰기
                TracePos("[bext 시작]");
                bw.Write(Encoding.ASCII.GetBytes(header.BextID ?? "bext"));   // "bext"
                bw.Write(header.BextChunkSize);                                // 크기
                bw.Write(ToFixedAscii(header.Bext.Description, 256));          // Description(256)
                bw.Write(ToFixedAscii(header.Bext.Originator, 32));            // Originator(32)
                bw.Write(ToFixedAscii(header.Bext.OriginatorReference, 32));   // OriginatorReference(32)
                bw.Write(ToFixedAscii(header.Bext.OriginationDate, 10));       // Date(10, "yyyy-MM-dd")
                bw.Write(ToFixedAscii(header.Bext.OriginationTime, 8));        // Time(8, "HH:mm:ss")
                bw.Write((uint)header.Bext.TimeReferenceLow);                  // TimeReference **Low** (리틀엔디언 규약)
                bw.Write((uint)header.Bext.TimeReferenceHigh);                 // TimeReference **High**
                bw.Write((ushort)header.Bext.Version);                         // Version(2)
                bw.Write(header.Bext.UMID);                                    // UMID(64)
                bw.Write(header.Bext.Reserved);                                // Reserved(190)
                bw.Write(ToFixedAscii(header.Bext.CodingHistory, 256));        // CodingHistory(256)
                                                                               // 15-1) bext 패딩(짝수 정렬)
                WritePadIfOdd(bw, header.BextChunkSize, "bext 패딩");
                TracePos("[bext 끝]");

                // 16) fmt 청크 쓰기
                TracePos("[fmt 시작]");
                bw.Write(Encoding.ASCII.GetBytes(header.FmtID ?? "fmt "));     // "fmt "
                bw.Write(header.FmtSize);                                       // 크기(실제 fmtData.Length)
                bw.Write(fmtData);                                             // 포맷 바이트
                                                                               // 16-1) fmt 패딩(짝수 정렬)
                WritePadIfOdd(bw, header.FmtSize, "fmt 패딩");
                TracePos("[fmt 끝]");

                // 17) fact/NDID 청크 쓰기
                TracePos("[fact 시작]");
                bw.Write(Encoding.ASCII.GetBytes(header.FactID ?? "fact"));    // "fact" 또는 "NDID"/"FTID" 등
                bw.Write(header.FactChunkSize);                                 // 크기
                                                                                // 17-1) FactRaw를 정확히 FactChunkSize만큼 기록(부족하면 0 패딩)
                int factWriteLen = (int)header.FactChunkSize;
                if (header.FactRaw.Length >= factWriteLen)
                {
                    // 충분하면 잘라서 기록
                    bw.Write(header.FactRaw, 0, factWriteLen);
                }
                else
                {
                    // 부족하면 Raw 모두 쓰고 0으로 채움
                    bw.Write(header.FactRaw);
                    bw.Write(new byte[factWriteLen - header.FactRaw.Length]);
                }
                // 17-2) fact 패딩(짝수 정렬)
                WritePadIfOdd(bw, header.FactChunkSize, "fact 패딩");
                TracePos("[fact 끝]");

                // 18) data 청크 쓰기
                TracePos("[data 시작]");
                bw.Write(Encoding.ASCII.GetBytes(header.DataID ?? "data"));    // "data"
                bw.Write(header.DataSize);                                      // PCM 크기
                if (pcmData != null && pcmData.Length > 0)
                {
                    // 실제 PCM 쓰기
                    bw.Write(pcmData);
                }
                // 18-1) data 패딩(짝수 정렬) — 주의: DataSize(헤더 값)는 패딩을 포함하지 않음
                WritePadIfOdd(bw, header.DataSize, "data 패딩");
                TracePos("[data 끝]");

                // 19) 디버그: 실제 쓴 PCM 바이트/현재 위치 출력
                Debug.WriteLine($"[data] 실제 쓴 byte 수(헤더 DataSize) = {header.DataSize}");
                Debug.WriteLine($"[data] 현재 파일 포인터 = {fs.Position} (0x{fs.Position:X})");

                // 20) 최종 파일 길이 계산 및 FileSize 필드 갱신(RIFF 규약: 전체길이-8)
                long finalLength = fs.Length;
                long riffChunkSize = finalLength - 8;                           // "RIFF" + FileSize(4)를 제외한 나머지
                if (riffChunkSize > uint.MaxValue)
                {
                    // 4GB 초과는 RF64를 써야 하므로 여기서는 안전하게 예외
                    throw new InvalidDataException("RIFF FileSize가 4GB를 초과했습니다. RF64 구현이 필요합니다.");
                }
                // 20-1) FileSize 덮어쓰기
                fs.Seek(riffSizePos, SeekOrigin.Begin);                        // FileSize 필드 위치로 이동
                bw.Write((uint)riffChunkSize);                                 // 최종 FileSize 기록
                bw.Flush();                                                    // 플러시
                fs.Flush(true);                                                // 디스크까지 플러시

                // 21) 최종 검증(길이)
                long actualSize = fs.Length;                                   // 실제 길이
                long expectedSize = ((uint)riffChunkSize) + 8;                 // 헤더에 쓴 값 + 8
                Debug.WriteLine($"[최종검증] Header(계산): {expectedSize}, Actual(fs.Length): {actualSize}");
                if (actualSize != expectedSize)
                {
                    // 길이 불일치 시 예외
                    throw new InvalidDataException($"File size mismatch after write. Header: {expectedSize}, Actual: {actualSize}");
                }
            }

            // 22) 2차 검증: 방금 쓴 파일을 자기 파서로 재파싱(헤더/길이 불일치 조기 탐지)
            try
            {
                // 파일 바이트 로드
                byte[] outBytes = File.ReadAllBytes(path);
                // 재파싱 시도(프로젝트의 표준 파서 호출)
                var _ = BwfFileInfo.ReadBwfFileInfoFromByte(outBytes);
                // 성공 로그
                System.Diagnostics.Debug.WriteLine("[자가검증] ReadBwfFileInfoFromByte 성공");
            }
            catch (Exception ex)
            {
                // 실패 로그 및 전파(상위에서 원인 파악 용이)
                System.Diagnostics.Debug.WriteLine($"[자가검증] 실패: {ex.GetType().Name} - {ex.Message}");
                throw;
            }
        }


        // ========================
        // BWF 파일 읽기 함수
        // ========================
        public static BwfHeader Read(string path)
        {
            if (!File.Exists(path)) return null;                                     // 파일이 존재하지 않으면 null 반환

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var br = new BinaryReader(fs, Encoding.ASCII))                    // BinaryReader로 읽기
            {
                var header = new BwfHeader();                                        // 새 헤더 객체 생성

                header.RiffID = Encoding.ASCII.GetString(br.ReadBytes(4));           // "RIFF" 확인
                header.FileSize = br.ReadUInt32();                                   // 전체 파일 크기 읽기 = wav 파일 (header + body)
                header.WaveID = Encoding.ASCII.GetString(br.ReadBytes(4));           // "WAVE" 확인

                long expectedSize = header.FileSize + 8L;
                if (fs.Length < expectedSize)
                    throw new InvalidDataException($"File length smaller than RIFF size. Header: {expectedSize}, Actual: {fs.Length}");

                while (br.BaseStream.Position < br.BaseStream.Length)                 // 청크 순서가 바뀌거나 없을 때를 대비
                {
                    string chunkID = Encoding.ASCII.GetString(br.ReadBytes(4));       // 다음 청크 ID 읽기
                    uint chunkSize = br.ReadUInt32();                                 // 청크 크기 읽기

                    if ((chunkSize % 2) != 0) chunkSize++;                            // 홀수 크기인 경우 패딩 추가

                    switch (chunkID)
                    {
                        // bext 청크
                        case "bext":

                            header.BextChunkSize = chunkSize;                                    // bext 크기 읽기
                            var bext = br.ReadBytes((int)chunkSize);                             // bext 전체 사이즈 읽기
                            header.Bext.Description = Encoding.ASCII.GetString(bext, 0, 256).TrimEnd('\0');
                            header.Bext.Originator = Encoding.ASCII.GetString(bext, 256, 32).TrimEnd('\0');
                            header.Bext.OriginatorReference = Encoding.ASCII.GetString(bext, 288, 32).TrimEnd('\0');
                            header.Bext.OriginationDate = Encoding.ASCII.GetString(bext, 320, 10).TrimEnd('\0');
                            header.Bext.OriginationTime = Encoding.ASCII.GetString(bext, 330, 8).TrimEnd('\0');
                            header.Bext.TimeReferenceHigh = BitConverter.ToUInt32(bext, 338);
                            header.Bext.TimeReferenceLow = BitConverter.ToUInt32(bext, 342);
                            header.Bext.Version = BitConverter.ToUInt16(bext, 346);
                            Array.Copy(bext, 348, header.Bext.UMID, 0, 64);
                            Array.Copy(bext, 412, header.Bext.Reserved, 0, 190);
                            header.Bext.CodingHistory = Encoding.ASCII.GetString(bext, 602, 256).TrimEnd('\0');
                            break;

                        // fmt 청크
                        case "fmt ":
                            header.FmtID = chunkID;
                            header.FmtSize = chunkSize;                                          // fmt 전체 사이즈 읽기

                            var fmt = br.ReadBytes((int)header.FmtSize);                         // 포맷 읽기
                            IntPtr fmtPtr = Marshal.AllocHGlobal(fmt.Length);                    // 메모리 할당
                            Marshal.Copy(fmt, 0, fmtPtr, fmt.Length);                            // 데이터 복사
                            header.Format = (WaveFormat)Marshal.PtrToStructure(fmtPtr, typeof(WaveFormat)); // 구조체로 역변환
                            Marshal.FreeHGlobal(fmtPtr);                                         // 메모리 해제
                            break;

                        // fact 청크
                        case "NDID":
                            header.FactID = chunkID;
                            header.FactChunkSize = chunkSize;
                            header.FactRaw = br.ReadBytes((int)chunkSize);            // fact 데이터 읽기
                            header.FactChunk = BwfHeader.ByteArrayToFactChunk(header.FactRaw);
                            break;

                        case "TFID":
                            {
                                header.FactID = chunkID;
                                header.FactChunkSize = chunkSize;

                                var tfidRaw = br.ReadBytes((int)chunkSize);
                                header.FactRaw = tfidRaw;

                                header.FactChunk = new FactChunk
                                {
                                    StartTime = FactChunkParser.ParseTFIDStartTime(tfidRaw)
                                };

                                // 패딩 처리(원래 로직대로)
                                if ((chunkSize & 1u) != 0) br.BaseStream.Seek(1, SeekOrigin.Current);
                                break;
                            }


                        // data 청크
                        case "data":
                            header.DataID = chunkID;
                            header.DataSize = chunkSize;
                            br.BaseStream.Seek(chunkSize, SeekOrigin.Current);
                            break;

                        default:
                            br.BaseStream.Seek(chunkSize, SeekOrigin.Current);
                            break;
                    }
                }

                return header;                                                       // 완성된 헤더 반환
            }
        }


        // ========================
        // BWF 파일 파싱 함수
        // ========================
        /// WAV → (timeMin, timeMax, samplingRate, audioLength) 유틸
        public static (DateTime timeMin, DateTime timeMax,
                        float fs, int audioLengthSec) ParseWavHeader(string wavPath)
        {
            var miBwfFileInfo = Writer.BwfFile.ReadBwfFileInfo(wavPath);

            var hdr = Read(wavPath);
            if (hdr == null) throw new FileNotFoundException("wav not found", wavPath);

            // ① 기준 시각 : bext(OriginatorDate+Time) → yyyy-MM-dd HH:mm:ss  
            DateTime timeMin;
            if (miBwfFileInfo.miBWfHeader.FactChunk.StartTime <= 0)
            {

                timeMin = DateTime.Now;
                DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
                TimeSpan span = timeMin - epoch;

                miBwfFileInfo.miBWfHeader.FactChunk.StartTime = (int)span.TotalSeconds;
                miBwfFileInfo.miBWfHeader.FactChunk.EndTime = miBwfFileInfo.miBWfHeader.FactChunk.StartTime + (int)(miBwfFileInfo.mu32SigDataCnt * 2 / miBwfFileInfo.miBWfHeader.Format.U32AvgBytesPerSec);
            }
            else if (!DateTime.TryParseExact(
                    $"{hdr.Bext.OriginationDate} {hdr.Bext.OriginationTime}",
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out timeMin))
            {
                // bext 가 없으면 fact.StartTime(TFID) → Unix sec
                int unix = hdr.FactChunk?.StartTime ?? 0;
                timeMin = unix == 0
                          ? File.GetCreationTime(wavPath)          // 최후의 수단
                          : new DateTime(1970, 1, 1).AddSeconds(unix);
            }




            // ② 길이(초) = dataSize / (fs * (bits/8) * ch)
            float fs = hdr.Format.SamplesPerSec;
            int bytes = (int)hdr.DataSize;
            int chan = hdr.Format.Channels;
            int bps = hdr.Format.BitsPerSample;
            int audioLenSec = (int)Math.Round(
                bytes / (fs * (bps / 8.0f) * chan));

            return (timeMin,
                    timeMin.AddSeconds(audioLenSec),
                    fs,
                    audioLenSec);
        }
    }
}
