using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DBImplant_N1toN2.Features.Raw.Parsers
{
    public static class DetectDatParser
    {
        public static float[] ReadDetectDat(string path)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return Array.Empty<float>();
            if (fi.Length % 4 != 0) throw new InvalidDataException($"Detect.dat length must be multiple of 4. len={fi.Length}");

            int n = checked((int)(fi.Length / 4));
            var arr = new float[n];

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: false);

            for (int i = 0; i < n; i++)
                arr[i] = br.ReadSingle(); // little-endian float32

            return arr;
        }
    }


    public static class NbListParser
    {
        public sealed record NbRow(
            string TypeText,
            int TypeCode,
            int StartFrame,
            int EndFrame,
            int ExtractSizeBytes,
            string[] Tokens);

        // 필요하면 CP949 강제도 가능: Encoding.GetEncoding(949)
        public static List<NbRow> ReadNbList(string path, Encoding? encoding = null)
        {
            encoding ??= Encoding.Default;

            var rows = new List<NbRow>();
            if (!File.Exists(path)) return rows;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            while (true)
            {
                var line = ReadLineRaw(fs);
                if (line == null) break;
                if (line.Length == 0) continue;

                line = TrimEnd(line, (byte)'\r', (byte)'\n');

                var tokens = SplitByNull(line, encoding)
                    .Select(t => t.Trim())
                    .Where(t => t.Length > 0)
                    .ToArray();

                // 혹시 진짜 공백/탭 텍스트면 fallback
                if (tokens.Length <= 1)
                {
                    var s = encoding.GetString(line).Trim();
                    if (s.Length == 0) continue;
                    tokens = s.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                }

                // C++는 최소 8토큰 이상을 가정(0:type, 3:size, 7:startFrame)
                if (tokens.Length < 8) continue;

                string typeText = tokens[0];
                int extractSize = TryParseInt(tokens.ElementAtOrDefault(3));
                int startFrame = TryParseInt(tokens.ElementAtOrDefault(7));

                if (extractSize <= 0 || startFrame < 0) continue;

                int endFrame = startFrame + (extractSize / 4); // C++와 동일

                int typeCode = typeText switch
                {
                    "협대역" => 1,
                    "DIFAR" => 2,
                    "협대역/DIFAR" => 2,
                    "BT" => 3,
                    "AN" => 4,
                    "DICASS" => 8,
                    _ => 0
                };

                rows.Add(new NbRow(typeText, typeCode, startFrame, endFrame, extractSize, tokens));
            }

            return rows;
        }

        private static byte[]? ReadLineRaw(FileStream fs)
        {
            if (fs.Position >= fs.Length) return null;

            var buf = new List<byte>(256);
            while (fs.Position < fs.Length)
            {
                int b = fs.ReadByte();
                if (b < 0) break;
                if (b == '\n') break;
                buf.Add((byte)b);
            }
            return buf.ToArray();
        }

        private static IEnumerable<string> SplitByNull(byte[] bytes, Encoding enc)
        {
            int start = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == 0)
                {
                    yield return start < i ? enc.GetString(bytes, start, i - start) : "";
                    start = i + 1;
                }
            }
            if (start < bytes.Length)
                yield return enc.GetString(bytes, start, bytes.Length - start);
        }

        private static byte[] TrimEnd(byte[] src, params byte[] trim)
        {
            int end = src.Length;
            while (end > 0 && trim.Contains(src[end - 1])) end--;
            return end == src.Length ? src : src.Take(end).ToArray();
        }

        private static int TryParseInt(string? s)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }


    public sealed record QuickShortPeriodSegment(
    uint SegmentId,
    long StartTime,
    long EndTime,
    long Length,
    float LDAx,
    float LDAy,
    uint DetectingType,
    uint FrameCount,
    float StartFreq,
    float EndFreq,
    int Mod,
    float CenterFreq,
    float BandWidth)
    {
        public const int ByteSize = 64;

        public static QuickShortPeriodSegment Read(BinaryReader br)
        {
            if (br.BaseStream.Length - br.BaseStream.Position < ByteSize)
                throw new EndOfStreamException($"SegmentRecord needs {ByteSize} bytes.");

            uint segId = br.ReadUInt32();
            long start = br.ReadInt64();
            long end = br.ReadInt64();
            long len = br.ReadInt64();
            float ldax = br.ReadSingle();
            float lday = br.ReadSingle();
            uint type = br.ReadUInt32();
            uint frameCnt = br.ReadUInt32();
            float f0 = br.ReadSingle();
            float f1 = br.ReadSingle();
            int mod = br.ReadInt32();
            float cf = br.ReadSingle();
            float bw = br.ReadSingle();

            return new(segId, start, end, len, ldax, lday, type, frameCnt, f0, f1, mod, cf, bw);
        }
    }

    public sealed record QuickShortPeriodEvent(
        int EventId,
        long StartTime,
        long EndTime,
        long EventLength,
        uint DetectingType,
        int SegmentCount,
        int AiSegmentNum,
        int TrSegmentNum,
        int UnkSegmentNum,
        uint ManualOrAuto,
        byte[] DirPathRaw,     // 255 bytes 그대로
        string? DirPathText)   // 디코딩 시도 결과(선택)
    {
        public const int ByteSize = 307;

        public static QuickShortPeriodEvent Read(BinaryReader br, Encoding? dirPathEncoding = null)
        {
            if (br.BaseStream.Length - br.BaseStream.Position < ByteSize)
                throw new EndOfStreamException($"EventRecord needs {ByteSize} bytes.");

            int eventId = br.ReadInt32();
            long start = br.ReadInt64();
            long end = br.ReadInt64();
            long evLen = br.ReadInt64();
            uint type = br.ReadUInt32();
            int segCount = br.ReadInt32();

            int ai = br.ReadInt32();
            int tr = br.ReadInt32();
            int unk = br.ReadInt32();

            uint manualAuto = br.ReadUInt32();
            byte[] dirRaw = br.ReadBytes(255);
            if (dirRaw.Length != 255) throw new EndOfStreamException("DirPathRaw needs 255 bytes.");

            string? text = null;
            if (dirPathEncoding != null)
            {
                int nul = Array.IndexOf(dirRaw, (byte)0);
                int len = nul >= 0 ? nul : dirRaw.Length;
                if (len > 0)
                    text = dirPathEncoding.GetString(dirRaw, 0, len);
            }

            return new(eventId, start, end, evLen, type, segCount, ai, tr, unk, manualAuto, dirRaw, text);
        }
    }

    public static class SpsListReader
    {
        // dirPathEncoding: 한국 Windows 환경이면 보통 CP949(=949) 쪽이 맞는 경우가 많음.
        // 필요 없으면 null로 두고 raw bytes만 들고 가면 됨.
        public static List<(QuickShortPeriodEvent Ev, List<QuickShortPeriodSegment> Segs)> ReadAll(
            string filePath,
            Encoding? dirPathEncoding = null)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            var result = new List<(QuickShortPeriodEvent, List<QuickShortPeriodSegment>)>();

            while (fs.Position < fs.Length)
            {
                // 원본 C++은 ReadQuickShortPeriodEvent()가 0이면 break.
                // C#은 남은 바이트가 부족하면 종료로 처리.
                if (fs.Length - fs.Position < QuickShortPeriodEvent.ByteSize)
                    break;

                var ev = QuickShortPeriodEvent.Read(br, dirPathEncoding);

                // 세그먼트가 터무니없이 크면 파일 깨짐 방지 (안전장치)
                if (ev.SegmentCount < 0 || ev.SegmentCount > 1_000_000)
                    throw new InvalidDataException($"Invalid SegmentCount: {ev.SegmentCount}");

                long need = (long)ev.SegmentCount * QuickShortPeriodSegment.ByteSize;
                if (fs.Length - fs.Position < need)
                    throw new EndOfStreamException($"Not enough bytes for {ev.SegmentCount} segments.");

                var segs = new List<QuickShortPeriodSegment>(ev.SegmentCount);
                for (int i = 0; i < ev.SegmentCount; i++)
                    segs.Add(QuickShortPeriodSegment.Read(br));

                result.Add((ev, segs));
            }

            return result;
        }
    }
}
