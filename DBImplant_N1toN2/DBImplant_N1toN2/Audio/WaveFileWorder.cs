// 이 코드는 기존 C++의 CUtilFileManage::WriteBwfHeaderToFile 및 ReadBwfHeaderFromFile 함수에서 변환됨
// 목적: BWF (Broadcast Wave Format) 파일을 생성 및 읽기하고 메타데이터 및 오디오 데이터를 순차적으로 기록하고 검증함
// 구조: RIFF → WAVE → bext → fmt → NDID/TFID → data 순서로 chunk를 처리하며, 포맷 오류 발생 시 예외를 발생시킴
//       헤더에 최소 fmt 청크는 있어야 하고, bext, Fact 청크는 선택적

/*
항목				C++ FILE* 버전				C++ CFile 버전				C# 대응 방식
타입				FILE* (stdio.h)				CFile* (MFC 클래스)			FileStream, BinaryReader, BinaryWriter (System.IO)
유효성 검사		fp == NULL					file == NULL + m_hFile		File.Exists(path) + null 검사로 경로 유효성 판단
읽기 함수		fread(...)					file->Read(...)				BinaryReader.ReadBytes, ReadInt32 등
파일 위치 이동	fseek(fp, offset, ...)		file->Seek(...)				FileStream.Seek(...)
에러 처리		ferror, feof				CFileException				예외 기반: try-catch 또는 메서드 실패 시 null 반환
메모리 관리		fopen / fclose				생성자/소멸자 자동			using FileStream, using BinaryReader/Writer 로 자동 Dispose
*/

/*
-전체 파일 크기의 제한-
        RIFF 포맷(WAV/BWF)은 FileSize 필드가 32비트 정수이기 때문에 최대 4 GB (2^32 - 1)로 제한됨.
        즉, WAV/BWF 하나의 파일로는 약 4GB를 초과할 수 없음.
        BWF는 기본 필수 chunk만 고정이고, 나머지는 벤더, 목적, 장비에 따라 자유롭게 추가/변형 가능함.
        특히 "bext" chunk 이후엔 사용자가 추가한 청크가 나올 수 있음.
        공식적으로 “헤더는 몇 바이트까지다”라는 절대 상한은 없음, 다만 일반적으로 다음과 같음:

            청크      	크기(바이트)	                설명
            RIFF	    12	                        "RIFF" + 크기 + "WAVE"
            bext	    602 ~ 602+ (길이에 따라)	    표준은 602~
            fmt	        18	                        표준 PCM 포맷 크기
            fact	    보통 512	                    NDID나 TFID 커스텀 크기 가능
            기타 청크	벤더에 따라 추가됨

1) Bext (Broadcast Extension Chunk)
    BWF만의 확장 메타데이터 청크.
    방송, 뉴스 제작 등에서 사용하는 정보 (예: 제작자, 제작 일시, U8UMID, 코딩 이력 등)를 담음.
    RIFF-WAVE의 확장으로 추가된 chunk이며, 표준 WAVE에는 없음.

2) fmt (Format Chunk)
    오디오 데이터의 기본 포맷 정보를 담고 있는 chunk.
    예: 샘플링 주파수, 비트 깊이, 채널 수, 압축 방식 등.
    거의 모든 WAV/BWF 파일에서 필수로 존재함.

3) fact (Fact Chunk)
    압축된 데이터나 벤더 확장 데이터를 위한 chunk.
    PCM의 경우엔 생략 가능하지만, NDID, TFID 등 벤더 전용 정보가 들어간 경우 이 청크에 기록됨.

    - NDID, TFID는 무엇인가?-
        1. NDID (LIGNEX1 포맷 확장 청크)
                LIGNEX1 같은 특정 **방산/통신 벤더(Vendor)**가 사용하는 fact 청크 이름이 "NDID".
                일반 WAV에서는 "fact"라는 이름을 사용하지만, NDID는 이를 대체하거나 확장한 형태.
                보통 NDID 청크 안에는 다음 같은 벤더 전용 정보가 들어감:
                    - start_time, end_time, start_usec, end_usec (전파 수신 시각)
                    - 분석 장비 고유 설정
                    - 커스터마이징된 포맷 구조

        2. TFID (TASCAM HD-P2 포맷 확장 청크)
                TASCAM HD-P2 같은 녹음 장비 브랜드가 정의한 "TFID" 청크.
                녹음 당시 날짜·시간 문자열이 고정 위치에 포함되어 있음.

4) PCM (N32Pulse-Code Modulation)
    오디오 데이터의 디지털 비압축 표준 형식.
    예: 16비트 모노 PCM은 2바이트 단위로 샘플이 저장되며, WAV/BWF에서 가장 흔히 사용됨.
    FormatTag = 1일 때 PCM임.

-청크 기반 바이너리 포맷-

    WAV/BWF 파일은 RIFF 포맷(순차적으로 청크를 저장하는 구조)

    Write 흐름:
        "RIFF" 문자열 → 파일 크기 → "WAVE" 문자열
        "bext" → bext 청크 크기 → 메타데이터
        "fmt " → fmt 크기 → WaveFormat 구조체
        "NDID" → fact 청크 크기 → fact 데이터
        "data" → 데이터 크기 → PCM 데이터

    Read 흐름:
        위와 정확히 동일한 순서로 읽어야 함.
        중간에 순서가 틀리면 청크 해석이 엉망이 됨 (즉, RIFF 파일 깨짐).
        각 chunk 마다 Encoding.ASCII.GetString(br.ReadBytes(4))로
        청크 ID를 먼저 확인한 다음 그 크기만큼 읽는 방식이 필수

-포팅 작업 이외 개선 필요 방향-

항목                          	        현 상태	                                                    예상 위험

1. U32BextChunkSize 불일치	                858로 고정	                                                Mismatch 발생 위험
2. FactRaw.Length != FactChunkSize	    FactRaw는 byte[512] 고정인데 FactChunkSize는 따로 있음	    FactChunk가 만약 512 넘어가면 오류로 넘어감
3. FactChunk Marshal 사이즈 불일치	    구조체가 512바이트라 가정하고 처리 중	                        필드 추가/삭제 시 변형됨
4. pcmData.Length != DataSize	        DataSize를 수동 설정	                                        값이 다르면 읽기/쓰기 Mismatch
5. 필드별 오프셋 고정값 사용          	bext 해석 등에서 offset 수동 계산	                            유연성 부족
6. 청크 순서 고정     	                "bext" → "fmt " → "NDID"        	                        순서가 바뀐 파일이 있다면 읽기 불가

 */

// BWF 파일 입출력 및 chunk 파싱 전체 구현 코드 (BwfHandler)
// Broadcast Wave Format의 구조적 저장 및 해석을 지원하는 C# 클래스 구성
// - 주요 chunk: RIFF, WAVE, bext, fmt, fact (NDID, TFID 등), data
// - 기능: 읽기(Read), 쓰기(Write), TFID 파싱(ParseTFIDStartTime)

using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Markup;

namespace DBImplant_N1toN2.Audio.Writer
{
    // [1] fmt chunk를 표현하는 구조체 - 오디오 포맷 정보 저장
    [StructLayout(LayoutKind.Explicit, Size = 18)]
    public struct WaveFormat
    {
        [FieldOffset(0)] public ushort FormatTag;         // 오디오 포맷 (1 = PCM)
        [FieldOffset(2)] public ushort Channels;          // 채널 수 (1 = 모노, 2 = 스테레오)
        [FieldOffset(4)] public uint U32SamplesPerSec;       // 초당 샘플 수
        [FieldOffset(8)] public uint U32AvgBytesPerSec;      // 초당 평균 바이트 수
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
            U8UMID = new byte[64];
            U8Reserved = new byte[190];
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

        public uint U32TimeReferenceLow;
        public uint U32TimeReferenceHigh;      // 오디오 시간 기준 위치

        public ushort N32Version = 1;          // BWF 버전 (보통 1)

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] U8UMID;                 // 고유 식별자 U8UMID

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 190)]
        public byte[] U8Reserved;            // 예약 필드 (0으로 패딩)

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

        public float FCenterFreq;        // center frequency
        public float FSaveSampleRate;    // sampling rate

        public float FamsGain;          // FAMS gain
        public float FamsCpaDistance;   // FAMS FCPA 거리.
        public float FamsCpaTime;       // FAMS FCPA 시간.

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 81)]
        public byte[] U8Comment;

        public byte U8InputRange;         // 0:default 1Vpp, 1:+/-2.5Vpp 2:+/-5Vpp 3:+/-10Vpp

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 382)]
        public byte[] U8Reserved;        // 필드 추가시 변경됨.

        // 유틸리티
        public string GetComment() => Encoding.ASCII.GetString(U8Comment).TrimEnd('\0');

        public void SetComment(string text)
        {
            U8Comment = new byte[81];
            var bytes = Encoding.ASCII.GetBytes(text ?? "");
            Array.Copy(bytes, U8Comment, Math.Min(bytes.Length, 81));
        }

        public DateTime GetStartTimeDate()
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddSeconds(StartTime)
                .AddTicks(StartUsecTime * 10).ToLocalTime(); // 1us = 10 ticks
        }

        public DateTime GetEndTimeDate()
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddSeconds(EndTime)
                .AddTicks(EndUsecTime * 10).ToLocalTime();
        }
    }

    // [4] 전체 BWF 헤더 클래스 정의 - 각 청크와 필드를 하나로 구성
    public class BwfHeader
    {
        public string RiffID = "RIFF";     // 파일 헤더 ID
        public uint U32FileSize;              // 전체 파일 크기 (bext~data 포함)
        public string WaveID = "WAVE";     // WAVE 형식 지정 ID

        public string BextID = "bext";     // 방송 메타데이터 청크 ID
        public uint U32BextChunkSize = 858;    // bext 크기 (기본값 858, C++ 참고)
        public BextChunk Bext = new BextChunk(); // bext 필드 집합

        public string FmtID = "fmt ";      // 포맷 청크 ID
        public uint U32FmtSize = 18;           // fmt 청크 크기
        public WaveFormat Format;          // WaveFormat 구조체

        public string FactID = "NDID";      // fact 청크 ID (vendor specific)
        public uint U32FactChunkSize = 512;    // fact 청크 크기
        public byte[] U8FactRaw = new byte[512]; // fact chunk 원본 그대로 저장

        public string DataID = "data";     // PCM 데이터 식별자
        public uint U32DataSize;              // 실제 음성 데이터 바이트 수

        // FactChunk - FactRaw를 byte[]로 변환하여 저장
        private FactChunk _factChunk;

        public FactChunk FactChunk
        {
            get
            {
                if (_factChunk == null && U8FactRaw?.Length >= 512) //기본값 512, FactRaw가 그것보다 더 크면 해당 길이로
                    _factChunk = ByteArrayToFactChunk(U8FactRaw);
                return _factChunk;
            }
            set
            {
                _factChunk = value;
                U8FactRaw = FactChunkToByteArray(value);
            }
        }

        // 바이트 배열을 FactChunk로 복원
        public static FactChunk ByteArrayToFactChunk(byte[] raw)
        {
            // raw가 512바이트보다 작으면(또는 null이면) 의미있는 FactChunk로 복원 불가 → 새 FactChunk 반환
            if (raw == null || raw.Length < 512)
                return new FactChunk();

            // GCHandle을 사용해 raw 배열을 고정 메모리로 핀(Pinned) → 포인터 주소로 접근 가능
            var handle = GCHandle.Alloc(raw, GCHandleType.Pinned);
            try
            {
                // 포인터에서 FactChunk 구조체로 직접 마샬링(역직렬화) 수행
                return (FactChunk)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(FactChunk));
            }
            finally
            {
                // 사용이 끝나면 handle 해제
                handle.Free();
            }
        }

        // FactChunk를 바이트 배열로 직렬화</param>
        public static byte[] FactChunkToByteArray(FactChunk chunk)
        {
            // FactChunk 구조체의 전체 바이트 크기를 확인
            var size = Marshal.SizeOf(typeof(FactChunk));

            // 직렬화 결과를 담을 바이트 배열을 구조체 크기만큼 할당
            var raw = new byte[size];

            // 구조체를 임시로 복사할 비관리 메모리 영역 할당
            var ptr = Marshal.AllocHGlobal(size);

            try
            {
                // FactChunk 구조체 → 비관리 메모리(ptr)로 마샬링(구조체 → 포인터)
                Marshal.StructureToPtr(chunk, ptr, true);

                // ptr에서 raw로 전체 바이트를 복사
                Marshal.Copy(ptr, raw, 0, size);

                // 생성된 byte[] 반환
                return raw;
            }
            finally
            {
                // 할당했던 비관리 메모리(ptr) 해제
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
            catch (ArgumentOutOfRangeException)
            {
                // 유효하지 않으면 0 반환
                return 0;
            }
            return 0;
        }
    }

    // YDY
    public class BwfFileInfo
    {
        public BwfHeader miBWfHeader;
        public Int32 mn32HeaderSize;
        public UInt32 mu32SigDataCnt;

        public short[] mn16SigData = null;
    }

    // YDY

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
            header.U32DataSize = (uint)(pcmData != null ? pcmData.Length : 0);           // PCM 데이터의 크기를 설정 (null이면 0)

            *//*
            수치	                    의미	                    상세 설명

            4           	        "WAVE"      	        WAVE 포맷 ID (4바이트)

            4           	        "bext"      	        bext 청크 ID
            4           	        U32BextChunkSize	        bext 청크 크기 (크기 필드)
            header.U32BextChunkSize	bext 내용 크기	        바로 위에 적힌 만큼의 크기

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
            header.U32FileSize = 1424 + (uint)(pcmData != null ? pcmData.Length : 0);

            Debug.WriteLine($"[U32FileSize 계산] header.U32FileSize = {header.U32FileSize}");

            header.Format.U32AvgBytesPerSec = header.Format.U32SamplesPerSec * header.Format.Channels * header.Format.BitsPerSample / 8; // 평균 바이트 계산

            // FactChunk → FactRaw 변환
            if (header.FactChunk != null)
                header.U8FactRaw = BwfHeader.FactChunkToByteArray(header.FactChunk);

            using (var fs = new FileStream(path, FileMode.Create))                   // 새 파일을 생성하고 FileStream 열기
            using (var bw = new BinaryWriter(fs, Encoding.ASCII))                    // BinaryWriter로 ASCII 형식의 파일 쓰기
            {
                void TracePos(string label)
                {
                    Debug.WriteLine($"{label,-10} 위치 = {fs.Position} (0x{fs.Position:X})");
                }

                bw.Write(Encoding.ASCII.GetBytes(header.RiffID));                    // "RIFF" 쓰기
                //bw.Write(header.U32FileSize);                                           // 파일 전체 크기 쓰기
                bw.Write(Encoding.ASCII.GetBytes(header.WaveID));                    // "WAVE" 쓰기
                TracePos("[RIFF]");

                // bext 쓰기
                TracePos("[bext 시작]");
                bw.Write(Encoding.ASCII.GetBytes(header.BextID));                    // "bext" 쓰기
                bw.Write(header.U32U32BextChunkSize);                                      // bext 크기 쓰기
                bw.Write(ToFixedAscii(header.Bext.Description, 256));                // 설명
                bw.Write(ToFixedAscii(header.Bext.Originator, 32));                  // 생성자
                bw.Write(ToFixedAscii(header.Bext.OriginatorReference, 32));         // 참조
                bw.Write(ToFixedAscii(header.Bext.OriginationDate, 10));             // 날짜
                bw.Write(ToFixedAscii(header.Bext.OriginationTime, 8));              // 시간
                bw.Write((uint)(header.Bext.U32TimeReferenceHigh));                     // 참조 시간 하위 32비트
                bw.Write((uint)(header.Bext.U32TimeReferenceLow));                      // 참조 시간 상위 32비트
                bw.Write(header.Bext.N32Version);                                       // 버전
                bw.Write(header.Bext.U8U8UMID);                                          // U8UMID 쓰기
                bw.Write(header.Bext.U8U8Reserved);                                      // 예약 공간
                bw.Write(ToFixedAscii(header.Bext.CodingHistory, 256));              // 코딩 히스토리
                TracePos("[bext 끝]");

                // fmt 쓰기
                TracePos("[fmt 시작]");
                *//*bw.Write(Encoding.ASCII.GetBytes(header.FmtID));                     // "fmt " 쓰기
                bw.Write(header.U32FmtSize);                                            // fmt chunk 크기 쓰기

                int size = Marshal.SizeOf(typeof(WaveFormat));                       // 구조체 크기 계산
                var fmtData = new byte[size];                                        // 포맷 데이터를 담을 배열
                var ptr = Marshal.AllocHGlobal(size);                                // 비관리 메모리 할당
                Marshal.StructureToPtr(header.Format, ptr, true);                    // 구조체를 포인터로 복사
                Marshal.Copy(ptr, fmtData, 0, size);                                 // 포인터 데이터를 배열로 복사
                Marshal.FreeHGlobal(ptr);                                            // 메모리 해제
                bw.Write(fmtData);                                                   // 포맷 데이터를 파일에 쓰기*//*
                // fmt 쓰기 (선언 크기만큼만 기록)
                bw.Write(Encoding.ASCII.GetBytes(header.FmtID));
                bw.Write(header.U32FmtSize);

                // WaveFormat 18바이트 생성
                int wfSize = Marshal.SizeOf(typeof(WaveFormat));
                var fmtData = new byte[wfSize];
                var ptr = Marshal.AllocHGlobal(wfSize);
                Marshal.StructureToPtr(header.Format, ptr, true);
                Marshal.Copy(ptr, fmtData, 0, wfSize);
                Marshal.FreeHGlobal(ptr);

                // 선언 크기(U32FmtSize)와 실제 생성 길이(18) 중 작은 값만 기록
                int fmtToWrite = (int)Math.Min(header.U32FmtSize, (uint)fmtData.Length);
                bw.Write(fmtData, 0, fmtToWrite);

                // fmt가 홀수면 패딩 1바이트
                if ((header.U32FmtSize & 1) == 1) bw.Write((byte)0);
                TracePos("[fmt 끝]");

                // fact 쓰기 (C++ 코드와 동일하게 NDID에 대한 쓰기만 하는 것으로 기대)
                // 먼저 FactRaw를 FactChunkSize에 맞춰 자름
                TracePos("[fact 시작]");
                if (header.U8FactRaw.Length > header.U32FactChunkSize)
                    header.U8FactRaw = header.U8FactRaw.Take((int)header.U32FactChunkSize).ToArray();
                bw.Write(Encoding.ASCII.GetBytes(header.FactID));                    // "NDID" 또는 다른 fact ID 쓰기
                bw.Write(header.U32FactChunkSize);                                      // fact chunk 크기 쓰기
                bw.Write(header.U8FactRaw.Take((int)header.U32FactChunkSize).ToArray());  // fact chunk 데이터 쓰기
                TracePos("[fact 끝]");

                TracePos("[data 시작]");
                *//*bw.Write(Encoding.ASCII.GetBytes(header.DataID));                    // "data" 쓰기
                bw.Write(header.U32DataSize);                                           // PCM 데이터 크기 쓰기
                if (pcmData != null)                                                 // PCM 데이터가 있다면
                    bw.Write(pcmData);                                               // PCM 데이터 쓰기*//*
                bw.Write(Encoding.ASCII.GetBytes(header.DataID));
                bw.Write(header.U32DataSize);
                if (pcmData != null) bw.Write(pcmData);

                // data 페이로드가 홀수면 1바이트 패딩
                if ((header.U32DataSize & 1) == 1) bw.Write((byte)0);
                TracePos("[data 끝]");

                Debug.WriteLine($"[data] 실제 쓴 byte 수 = {header.U32DataSize}");
                Debug.WriteLine($"[data] 위치 = {fs.Position} (0x{fs.Position:X})");

                static uint Pad(uint n) => (n & 1u) == 1u ? 1u : 0u;

                uint bext = header.U32U32BextChunkSize;
                uint fmt = header.U32FmtSize;
                uint fact = header.U32FactChunkSize;
                uint data = header.U32DataSize;

                // RIFF size = "WAVE"(4) + Σ(각 청크: 8 + size + pad)
                header.U32FileSize =
                      4
                    + (8 + bext + Pad(bext))
                    + (8 + fmt + Pad(fmt))
                    + (8 + fact + Pad(fact))
                    + (8 + data + Pad(data));

                bw.Write(header.U32FileSize);
            }            

            // 파일 크기 검증 (디버깅 목적)
            long actualSize = new FileInfo(path).Length;
            long expectedSize = header.U32FileSize + 8; // RIFF ID(4) + FileSize(4)
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

            // 4) BEXT 필드의 필수 배열(U8UMID/U8Reserved) 널 방지 및 길이 보정
            if (header.Bext.U8UMID == null || header.Bext.U8UMID.Length != 64)
            {
                // 64바이트 U8UMID 보정
                header.Bext.U8UMID = (header.Bext.U8UMID ?? new byte[0]).Take(64).Concat(new byte[64]).Take(64).ToArray();
            }
            if (header.Bext.U8Reserved == null || header.Bext.U8Reserved.Length != 190)
            {
                // 190바이트 U8Reserved 보정
                header.Bext.U8Reserved = (header.Bext.U8Reserved ?? new byte[0]).Take(190).Concat(new byte[190]).Take(190).ToArray();
            }

            // 5) 실제로 쓸 bext 페이로드 길이 계산(아래와 정확히 동일하게 써야 함)
            //    Description(256) + Originator(32) + OriginatorReference(32) + Date(10) + Time(8)
            //    + TimeReference Low(4) + High(4) + Version(2) + U8UMID(64) + U8Reserved(190) + CodingHistory(256)
            uint bextPayloadSize =
                256u + 32u + 32u + 10u + 8u + 4u + 4u + 2u + 64u + 190u + 256u;

            // 6) bext 청크 크기 동기화
            header.U32BextChunkSize = bextPayloadSize;

            // 7) fact 원시 데이터 준비(널 허용 → 빈 배열)
            if (header.FactChunk != null && (header.U8FactRaw == null || header.U8FactRaw.Length == 0))
            {
                // NDID 등 구조체가 있다면 Raw로 직렬화
                header.U8FactRaw = BwfHeader.FactChunkToByteArray(header.FactChunk);
            }
            if (header.U8FactRaw == null) header.U8FactRaw = new byte[0];

            // 8) fact 쓰기 길이 결정: FactChunkSize가 0이면 Raw 길이로 동기화
            if (header.U32FactChunkSize == 0)
            {
                // Raw 길이를 크기 필드에 반영
                header.U32FactChunkSize = (uint)header.U8FactRaw.Length;
            }

            // 9) PCM 길이 계산 및 DataSize 동기화
            uint dataSize = (uint)((pcmData != null) ? pcmData.Length : 0);
            header.U32DataSize = dataSize;

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
            header.U32FmtSize = (uint)fmtData.Length;

            // 12) AvgBytesPerSec 재계산(포맷 정보가 맞지 않으면 지속적으로 어긋남)
            header.Format.U32AvgBytesPerSec =
                header.Format.U32SamplesPerSec * header.Format.Channels * header.Format.BitsPerSample / 8;

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
                bw.Write(header.U32BextChunkSize);                                // 크기
                bw.Write(ToFixedAscii(header.Bext.Description, 256));          // Description(256)
                bw.Write(ToFixedAscii(header.Bext.Originator, 32));            // Originator(32)
                bw.Write(ToFixedAscii(header.Bext.OriginatorReference, 32));   // OriginatorReference(32)
                bw.Write(ToFixedAscii(header.Bext.OriginationDate, 10));       // Date(10, "yyyy-MM-dd")
                bw.Write(ToFixedAscii(header.Bext.OriginationTime, 8));        // Time(8, "HH:mm:ss")
                bw.Write((uint)header.Bext.U32TimeReferenceLow);                  // TimeReference **Low** (리틀엔디언 규약)
                bw.Write((uint)header.Bext.U32TimeReferenceHigh);                 // TimeReference **High**
                bw.Write((ushort)header.Bext.N32Version);                         // Version(2)
                bw.Write(header.Bext.U8UMID);                                    // U8UMID(64)
                bw.Write(header.Bext.U8Reserved);                                // U8Reserved(190)
                bw.Write(ToFixedAscii(header.Bext.CodingHistory, 256));        // CodingHistory(256)
                                                                               // 15-1) bext 패딩(짝수 정렬)
                WritePadIfOdd(bw, header.U32BextChunkSize, "bext 패딩");
                TracePos("[bext 끝]");

                // 16) fmt 청크 쓰기
                TracePos("[fmt 시작]");
                bw.Write(Encoding.ASCII.GetBytes(header.FmtID ?? "fmt "));     // "fmt "
                bw.Write(header.U32FmtSize);                                       // 크기(실제 fmtData.Length)
                bw.Write(fmtData);                                             // 포맷 바이트
                                                                               // 16-1) fmt 패딩(짝수 정렬)
                WritePadIfOdd(bw, header.U32FmtSize, "fmt 패딩");
                TracePos("[fmt 끝]");

                // 17) fact/NDID 청크 쓰기
                TracePos("[fact 시작]");
                bw.Write(Encoding.ASCII.GetBytes(header.FactID ?? "fact"));    // "fact" 또는 "NDID"/"FTID" 등
                bw.Write(header.U32FactChunkSize);                                 // 크기
                                                                                   // 17-1) FactRaw를 정확히 FactChunkSize만큼 기록(부족하면 0 패딩)
                int factWriteLen = (int)header.U32FactChunkSize;
                if (header.U8FactRaw.Length >= factWriteLen)
                {
                    // 충분하면 잘라서 기록
                    bw.Write(header.U8FactRaw, 0, factWriteLen);
                }
                else
                {
                    // 부족하면 Raw 모두 쓰고 0으로 채움
                    bw.Write(header.U8FactRaw);
                    bw.Write(new byte[factWriteLen - header.U8FactRaw.Length]);
                }
                // 17-2) fact 패딩(짝수 정렬)
                WritePadIfOdd(bw, header.U32FactChunkSize, "fact 패딩");
                TracePos("[fact 끝]");

                // 18) data 청크 쓰기
                TracePos("[data 시작]");
                bw.Write(Encoding.ASCII.GetBytes(header.DataID ?? "data"));    // "data"
                bw.Write(header.U32DataSize);                                      // PCM 크기
                if (pcmData != null && pcmData.Length > 0)
                {
                    // 실제 PCM 쓰기
                    bw.Write(pcmData);
                }
                // 18-1) data 패딩(짝수 정렬) — 주의: DataSize(헤더 값)는 패딩을 포함하지 않음
                WritePadIfOdd(bw, header.U32DataSize, "data 패딩");
                TracePos("[data 끝]");

                // 19) 디버그: 실제 쓴 PCM 바이트/현재 위치 출력
                Debug.WriteLine($"[data] 실제 쓴 byte 수(헤더 DataSize) = {header.U32DataSize}");
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
                var _ = ReadBwfFileInfoFromByte(outBytes);
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

        // YDY
        public static BwfFileInfo ReadBwfFileInfo(string path)
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

        public static BwfFileInfo ReadBwfFileInfoFromByte(byte[] data)
        {
            BwfFileInfo iBwfFileInfo = new BwfFileInfo();

            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms, Encoding.ASCII))                    // BinaryReader로 읽기
            {
                var header = new BwfHeader();                                        // 새 헤더 객체 생성

                iBwfFileInfo.mn32HeaderSize = 0;

                header.RiffID = Encoding.ASCII.GetString(br.ReadBytes(4));           // "RIFF" 확인
                iBwfFileInfo.mn32HeaderSize += 4;
                header.U32FileSize = br.ReadUInt32();                                   // 전체 파일 크기 읽기 = wav 파일 (header + body)
                iBwfFileInfo.mn32HeaderSize += 4;
                header.WaveID = Encoding.ASCII.GetString(br.ReadBytes(4));           // "WAVE" 확인
                iBwfFileInfo.mn32HeaderSize += 4;

                long expectedSize = header.U32FileSize + 8;                             // FileSize 필드와 실제 파일 크기 검증
                // 트레일링 바이트는 허용하고, 과소(파일이 헤더 신고치보다 작음)만 오류 처리 (BWFReader와 판정 일치)
                if (ms.Length < expectedSize)
                    throw new InvalidDataException($"File length smaller than RIFF size. Header: {expectedSize}, Actual: {ms.Length}");

                while (br.BaseStream.Position < br.BaseStream.Length)                 // 청크 순서가 바뀌거나 없을 때를 대비
                {
                    string chunkID = Encoding.ASCII.GetString(br.ReadBytes(4));       // 다음 청크 ID 읽기
                    iBwfFileInfo.mn32HeaderSize += 4;

                    uint chunkSize = br.ReadUInt32();                                 // 청크 크기 읽기
                    iBwfFileInfo.mn32HeaderSize += 4;

                    if ((chunkSize % 2) != 0) chunkSize++;                            // 홀수 크기인 경우 패딩 추가

                    switch (chunkID)
                    {
                        // bext 청크
                        case "bext":

                            header.U32BextChunkSize = chunkSize;                                    // bext 크기 읽기
                            var bext = br.ReadBytes((int)chunkSize);                             // bext 전체 사이즈 읽기
                            iBwfFileInfo.mn32HeaderSize += (int)chunkSize;

                            header.Bext.Description = Encoding.ASCII.GetString(bext, 0, 256).TrimEnd('\0');
                            header.Bext.Originator = Encoding.ASCII.GetString(bext, 256, 32).TrimEnd('\0');
                            header.Bext.OriginatorReference = Encoding.ASCII.GetString(bext, 288, 32).TrimEnd('\0');
                            header.Bext.OriginationDate = Encoding.ASCII.GetString(bext, 320, 10).TrimEnd('\0');
                            header.Bext.OriginationTime = Encoding.ASCII.GetString(bext, 330, 8).TrimEnd('\0');
                            // BWF 규격(EBU Tech 3285): TimeReference는 64비트 값이며
                            // offset 338 = TimeReferenceLow(하위 32비트), offset 342 = TimeReferenceHigh(상위 32비트).
                            // 쓰기 루틴(Write)도 Low → High 순으로 기록하므로 읽기와 방향 일치.
                            header.Bext.U32TimeReferenceLow = BitConverter.ToUInt32(bext, 338);
                            header.Bext.U32TimeReferenceHigh = BitConverter.ToUInt32(bext, 342);
                            header.Bext.N32Version = BitConverter.ToUInt16(bext, 346);
                            Array.Copy(bext, 348, header.Bext.U8UMID, 0, 64);
                            Array.Copy(bext, 412, header.Bext.U8Reserved, 0, 190);
                            header.Bext.CodingHistory = Encoding.ASCII.GetString(bext, 602, 256).TrimEnd('\0');
                            break;

                        // fmt 청크
                        case "fmt ":
                            header.FmtID = chunkID;
                            header.U32FmtSize = chunkSize;                                          // fmt 전체 사이즈 읽기

                            var fmt = br.ReadBytes((int)header.U32FmtSize);                         // 포맷 읽기
                            iBwfFileInfo.mn32HeaderSize += (int)header.U32FmtSize;

                            IntPtr fmtPtr = Marshal.AllocHGlobal(fmt.Length);                    // 메모리 할당
                            Marshal.Copy(fmt, 0, fmtPtr, fmt.Length);                            // 데이터 복사
                            header.Format = (WaveFormat)Marshal.PtrToStructure(fmtPtr, typeof(WaveFormat)); // 구조체로 역변환
                            Marshal.FreeHGlobal(fmtPtr);                                         // 메모리 해제
                            break;

                        // fact 청크
                        case "NDID":
                            header.FactID = chunkID;
                            header.U32FactChunkSize = chunkSize;
                            header.U8FactRaw = br.ReadBytes((int)chunkSize);                      // fact 데이터 읽기
                            iBwfFileInfo.mn32HeaderSize += (int)chunkSize;

                            header.FactChunk = BwfHeader.ByteArrayToFactChunk(header.U8FactRaw);
                            break;

                        case "TFID":
                            {
                                header.FactID = chunkID;
                                header.U32FactChunkSize = chunkSize;   // (수정) U32BextChunkSize 복붙 버그 → fact 청크 크기 필드에 기록

                                var tfidRaw = br.ReadBytes((int)chunkSize);
                                header.U8FactRaw = tfidRaw;

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
                            header.U32DataSize = chunkSize;

                            iBwfFileInfo.mu32SigDataCnt = (UInt32)(header.U32DataSize / 2);

                            byte[] byteTempBuffer = br.ReadBytes((int)chunkSize);

                            iBwfFileInfo.mn16SigData = new Int16[iBwfFileInfo.mu32SigDataCnt];

                            Buffer.BlockCopy(byteTempBuffer, 0, iBwfFileInfo.mn16SigData, 0, byteTempBuffer.Length);
                            //br.BaseStream.Seek(chunkSize, SeekOrigin.Current);

                            //double dRange = 0.0;
                            //switch (header.FactChunk.U8InputRange)
                            //{
                            //    case 0:
                            //        dRange = 1;
                            //        break;

                            //    case 1:
                            //        dRange = 2.5;
                            //        break;

                            //    case 2:
                            //        dRange = 5;
                            //        break;

                            //    case 3:
                            //        dRange = 10;
                            //        break;

                            //    default:
                            //        dRange = 1;
                            //        break;
                            //}

                            //for (Int32 n32Index = 0; n32Index < iBwfFileInfo.mu32SigDataCnt; n32Index++)
                            //{
                            //    iBwfFileInfo.mdSigData[n32Index] = n16SigDataBuffer[n32Index] / 32768.0 * dRange;
                            //}

                            // fmt 청크 미등장 또는 AvgBytesPerSec==0 상태 가드(0 나눗셈 금지 → 명시 오류)
                            if (header.Format.U32AvgBytesPerSec == 0)
                                throw new InvalidDataException(
                                    "fmt 청크가 없거나 AvgBytesPerSec=0 이라 data 길이를 시간으로 환산할 수 없습니다.");

                            header.FactChunk.EndTime = header.FactChunk.StartTime + (Int32)(header.U32DataSize / header.Format.U32AvgBytesPerSec);
                            break;

                        default:
                            br.BaseStream.Seek(chunkSize, SeekOrigin.Current);
                            break;
                    }
                }

                iBwfFileInfo.miBWfHeader = header;

                return iBwfFileInfo;                                                       // 완성된 헤더 반환
            }
        }

        // YDY

        public static BwfHeader Read(string path)
        {
            // 파일이 존재하지 않으면 null 반환
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] data = File.ReadAllBytes(path);
            return ReadFromBytes(data);
        }

        public static BwfHeader Read(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            return ReadFromBytes(data);
        }

        static void SkipPadIfAny(Stream s, uint declaredSize)
        {
            if ((declaredSize & 1u) == 1u)
                s.Seek(1, SeekOrigin.Current); // 패딩 1바이트
        }


        private static BwfHeader ReadFromBytes(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms, Encoding.ASCII))                    // BinaryReader로 읽기
            {
                var header = new BwfHeader();                                        // 새 헤더 객체 생성

                header.RiffID = Encoding.ASCII.GetString(br.ReadBytes(4));           // "RIFF" 확인
                header.U32FileSize = br.ReadUInt32();                                   // 전체 파일 크기 읽기 = wav 파일 (header + body)
                header.WaveID = Encoding.ASCII.GetString(br.ReadBytes(4));           // "WAVE" 확인

                long expectedSize = header.U32FileSize + 8;                             // FileSize 필드와 실제 파일 크기 검증
                // 트레일링 바이트는 허용하고, 과소(파일이 헤더 신고치보다 작음)만 오류 처리 (BWFReader와 판정 일치)
                if (ms.Length < expectedSize)
                    throw new InvalidDataException($"File length smaller than RIFF size. Header: {expectedSize}, Actual: {ms.Length}");

                while (br.BaseStream.Position < br.BaseStream.Length)                 // 청크 순서가 바뀌거나 없을 때를 대비
                {
                    string chunkID = Encoding.ASCII.GetString(br.ReadBytes(4));       // 다음 청크 ID 읽기                    
                    uint chunkSize = br.ReadUInt32();                                 // 청크 크기 읽기
                    if ((chunkSize % 2) != 0) chunkSize++;                            // 홀수 크기인 경우 패딩 추가

                    switch (chunkID)
                    {
                        // bext 청크
                        case "bext":


                            header.U32BextChunkSize = chunkSize;                                    // bext 크기 읽기
                            var bext = br.ReadBytes((int)chunkSize);                             // bext 전체 사이즈 읽기
                            header.Bext.Description = Encoding.ASCII.GetString(bext, 0, 256).TrimEnd('\0');
                            header.Bext.Originator = Encoding.ASCII.GetString(bext, 256, 32).TrimEnd('\0');
                            header.Bext.OriginatorReference = Encoding.ASCII.GetString(bext, 288, 32).TrimEnd('\0');
                            header.Bext.OriginationDate = Encoding.ASCII.GetString(bext, 320, 10).TrimEnd('\0');
                            header.Bext.OriginationTime = Encoding.ASCII.GetString(bext, 330, 8).TrimEnd('\0');
                            // BWF 규격(EBU Tech 3285): TimeReference는 64비트 값이며
                            // offset 338 = TimeReferenceLow(하위 32비트), offset 342 = TimeReferenceHigh(상위 32비트).
                            // 쓰기 루틴(Write)도 Low → High 순으로 기록하므로 읽기와 방향 일치.
                            header.Bext.U32TimeReferenceLow = BitConverter.ToUInt32(bext, 338);
                            header.Bext.U32TimeReferenceHigh = BitConverter.ToUInt32(bext, 342);
                            header.Bext.N32Version = BitConverter.ToUInt16(bext, 346);
                            Array.Copy(bext, 348, header.Bext.U8UMID, 0, 64);
                            Array.Copy(bext, 412, header.Bext.U8Reserved, 0, 190);
                            header.Bext.CodingHistory = Encoding.ASCII.GetString(bext, 602, 256).TrimEnd('\0');
                            break;

                        // fmt 청크
                        case "fmt ":
                            header.FmtID = chunkID;

                            header.U32FmtSize = chunkSize;                                          // fmt 전체 사이즈 읽기
                            var fmt = br.ReadBytes((int)header.U32FmtSize);                         // 포맷 읽기
                            IntPtr fmtPtr = Marshal.AllocHGlobal(fmt.Length);                    // 메모리 할당
                            Marshal.Copy(fmt, 0, fmtPtr, fmt.Length);                            // 데이터 복사
                            header.Format = (WaveFormat)Marshal.PtrToStructure(fmtPtr, typeof(WaveFormat)); // 구조체로 역변환
                            Marshal.FreeHGlobal(fmtPtr);                                         // 메모리 해제
                            break;

                        // fact 청크
                        case "NDID":
                            header.FactID = chunkID;
                            header.U32FactChunkSize = chunkSize;
                            header.U8FactRaw = br.ReadBytes((int)chunkSize);                      // fact 데이터 읽기
                            header.FactChunk = BwfHeader.ByteArrayToFactChunk(header.U8FactRaw);
                            break;

                        case "TFID":
                            {
                                header.FactID = chunkID;
                                header.U32FactChunkSize = chunkSize;   // (수정) U32BextChunkSize 복붙 버그 → fact 청크 크기 필드에 기록

                                var tfidRaw = br.ReadBytes((int)chunkSize);
                                header.U8FactRaw = tfidRaw;

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
                            header.U32DataSize = chunkSize;
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
    }
}