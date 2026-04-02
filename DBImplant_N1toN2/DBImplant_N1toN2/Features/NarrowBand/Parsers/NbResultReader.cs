using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static DBImplant_N1toN2.Features.ShortPeriod.Parsers.EventListReader;

namespace DBImplant_N1toN2.Features.NarrowBand.Parsers
{
    public static class NbResultReader
    {
        // =========================
        // Models
        // =========================
        public sealed class NbFeatureInfo
        {
            // 기존 필드명 유지하고 싶으면 소문자도 가능하지만, 어차피 UI에 안 쓰면 PascalCase 추천
            public Header header { get; set; } = new Header();
            public int Count { get; set; }
            public List<NbFeature> Features { get; } = new List<NbFeature>();
        }

        public sealed class NbFeature
        {
            public int FeatureNumber { get; set; }
            public int StartTime { get; set; }
            public int EndTime { get; set; }

            public double StartFreq { get; set; }
            public double EndFreq { get; set; }
            public double CenterFreq { get; set; }
            public double FundamentalFreq { get; set; }
            public double Resolution { get; set; }

            public string Stable { get; set; } = "";
            public int LineWidth { get; set; }
            public byte LineType { get; set; }

            public string HarmonicList { get; set; } = "";
            public string NoiseSource { get; set; } = "";
            public string NoiseSource2 { get; set; } = "";
            public string Gram { get; set; } = "";
            public double SourceLevel { get; set; }

            public string CPA { get; set; } = "";
            public int Hypersis { get; set; }
            public string Comment { get; set; } = "";
            public string GramFileName { get; set; } = "";
            public string AudioFileName { get; set; } = "";

            public double Speed { get; set; }
            public double Score { get; set; }
            public byte AppliedState { get; set; }
            public int DbApplyId { get; set; }
            public byte GramAppliedState { get; set; }
        }

        public sealed class NbClassificationResult
        {
            public Header header { get; set; } = new Header();
            public int Count { get; set; }
            public List<NbHypResult> HypResults { get; } = new List<NbHypResult>();
        }

        public sealed class NbHypResult
        {
            public int HypNumber { get; set; }

            public string ClassName { get; set; } = "";
            public string ClassEName { get; set; } = "";
            public string VesselName { get; set; } = "";
            public string VesselEName { get; set; } = "";

            public float Score { get; set; }
            public float Speed { get; set; }
            public float MatchingRate { get; set; }

            public string DetectionSeaAreaLatitude { get; set; } = "";
            public string DetectionSeaAreaLongitude { get; set; } = "";
            public float Distance { get; set; }

            public int BladeNum { get; set; }
            public string Comment { get; set; } = "";

            public int GramSnippetNum { get; set; }
            public int AudioSnippetNum { get; set; }

            public byte AppliedState { get; set; }
            public string DbAppliedDate { get; set; } = "";
        }

        public sealed class ScoringParam
        {
            public int Hyp { get; set; }
            public string ShipSub { get; set; } = "";
            public string ClassName { get; set; } = "";
            public string VesselName { get; set; } = "";
            public string DetPlatform { get; set; } = "";
            public string SpeedLow { get; set; } = "";
            public string SpeedHigh { get; set; } = "";
        }

        // =========================
        // Public APIs
        // =========================
        public static NbFeatureInfo ReadNbFeatureInfoFea(string filePath, Encoding? enc = null)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException(filePath);
            enc ??= Encoding.GetEncoding(949);

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Header: 80+30+30+10+20+8+8 = 186
            var headerBuf = ReadExact(fs, 80 + 30 + 30 + 10 + 20 + 8 + 8);
            SkipNewline(fs);

            var info = new NbFeatureInfo();
            info.header.RawAcousticDataFileName = Decode(enc, headerBuf, 0, 80);
            info.header.DetectSystem = Decode(enc, headerBuf, 80, 30);
            info.header.DetectShipName = Decode(enc, headerBuf, 110, 30);
            info.header.DetectSonar = Decode(enc, headerBuf, 140, 10);
            info.header.DetectDate = Decode(enc, headerBuf, 150, 20);
            info.header.AnalyzerID = Decode(enc, headerBuf, 170, 8);
            info.header.ClassificationEquipment = Decode(enc, headerBuf, 178, 8);

            // Count: 8
            var countBuf = ReadExact(fs, 8);
            SkipNewline(fs);
            info.Count = ParseInt(enc.GetString(countBuf));

            // Record line: 893 bytes each
            const int recLen = 893;

            for (int i = 0; i < info.Count; i++)
            {
                var rec = ReadExact(fs, recLen);
                SkipNewline(fs);

                var f = new NbFeature();

                // Segment A (0..119)
                f.FeatureNumber = ParseInt(Decode(enc, rec, 0, 8));
                f.StartTime = ParseInt(Decode(enc, rec, 8, 16));
                f.EndTime = ParseInt(Decode(enc, rec, 24, 16));
                f.StartFreq = ParseDouble(Decode(enc, rec, 40, 16));
                f.EndFreq = ParseDouble(Decode(enc, rec, 56, 16));
                f.CenterFreq = ParseDouble(Decode(enc, rec, 72, 16));
                f.FundamentalFreq = ParseDouble(Decode(enc, rec, 88, 16));
                f.Resolution = ParseDouble(Decode(enc, rec, 104, 16));

                // Segment B (120..435)
                f.Stable = Decode(enc, rec, 120, 6);
                f.LineWidth = ParseInt(Decode(enc, rec, 126, 10));
                f.LineType = ParseByte(Decode(enc, rec, 136, 2));
                f.HarmonicList = Decode(enc, rec, 138, 240);
                f.NoiseSource = Decode(enc, rec, 378, 20);
                f.NoiseSource2 = Decode(enc, rec, 398, 20);
                f.Gram = Decode(enc, rec, 418, 2);
                f.SourceLevel = ParseDouble(Decode(enc, rec, 420, 16));

                // Segment C (436..846)
                f.CPA = Decode(enc, rec, 436, 3);
                f.Hypersis = ParseInt(Decode(enc, rec, 439, 8));
                f.Comment = Decode(enc, rec, 447, 240);
                f.GramFileName = Decode(enc, rec, 687, 80);
                f.AudioFileName = Decode(enc, rec, 767, 80);

                // Segment D (847..892)
                f.Speed = ParseDouble(Decode(enc, rec, 847, 16));
                f.Score = ParseDouble(Decode(enc, rec, 863, 16));
                f.AppliedState = ParseByte(Decode(enc, rec, 879, 2));
                f.DbApplyId = ParseInt(Decode(enc, rec, 881, 10));
                f.GramAppliedState = ParseByte(Decode(enc, rec, 891, 2));

                info.Features.Add(f);
            }

            return info;
        }

        public static NbClassificationResult ReadNbClassificationCla(string filePath, Encoding? enc = null)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException(filePath);
            enc ??= Encoding.GetEncoding(949);

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var result = new NbClassificationResult();

            // Header: 80+30+30+30+20+8+8 = 206
            var headerBuf = ReadExact(fs, 80 + 30 + 30 + 30 + 20 + 8 + 8);
            SkipNewline(fs);

            result.header.RawAcousticDataFileName = Decode(enc, headerBuf, 0, 80);
            result.header.DetectSystem = Decode(enc, headerBuf, 80, 30);
            result.header.DetectShipName = Decode(enc, headerBuf, 110, 30);
            result.header.DetectSonar = Decode(enc, headerBuf, 140, 30);
            result.header.DetectDate = Decode(enc, headerBuf, 170, 20);
            result.header.AnalyzerID = Decode(enc, headerBuf, 190, 8);
            result.header.ClassificationEquipment = Decode(enc, headerBuf, 198, 8);

            // Count: 8
            var countBuf = ReadExact(fs, 8);
            SkipNewline(fs);

            result.Count = ParseInt(enc.GetString(countBuf));

            // Record: 352 bytes (newline 제외)
            const int recLen = 352;

            for (int i = 0; i < result.Count; i++)
            {
                var rec = ReadExact(fs, recLen);
                SkipNewline(fs);

                var r = new NbHypResult();

                r.HypNumber = ParseInt(Decode(enc, rec, 0, 8));
                r.ClassName = Decode(enc, rec, 8, 30);
                r.ClassEName = Decode(enc, rec, 38, 30);
                r.VesselName = Decode(enc, rec, 68, 30);
                r.VesselEName = Decode(enc, rec, 98, 30);

                r.Score = ParseFloat(Decode(enc, rec, 128, 16));
                r.Speed = ParseFloat(Decode(enc, rec, 144, 16));
                r.MatchingRate = ParseFloat(Decode(enc, rec, 160, 16));

                r.DetectionSeaAreaLatitude = Decode(enc, rec, 176, 20);
                r.DetectionSeaAreaLongitude = Decode(enc, rec, 196, 20);

                r.Distance = ParseFloat(Decode(enc, rec, 216, 16));
                r.BladeNum = ParseInt(Decode(enc, rec, 232, 2));

                r.Comment = Decode(enc, rec, 234, 80);

                r.GramSnippetNum = ParseInt(Decode(enc, rec, 314, 8));
                r.AudioSnippetNum = ParseInt(Decode(enc, rec, 322, 8));

                r.AppliedState = ParseByte(Decode(enc, rec, 330, 2));
                r.DbAppliedDate = Decode(enc, rec, 332, 20);

                result.HypResults.Add(r);
            }

            return result;
        }

        public static List<ScoringParam> ReadScoringParamDat(string filePath, Encoding? enc = null)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException(filePath);
            enc ??= Encoding.GetEncoding(949);

            var list = new List<ScoringParam>();

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            int nCnt = br.ReadInt32();

            for (int i = 0; i < nCnt; i++)
            {
                var p = new ScoringParam();
                p.Hyp = br.ReadInt32();

                p.ShipSub = DecodeFixedNullTrim(enc, br.ReadBytes(13));
                p.ClassName = DecodeFixedNullTrim(enc, br.ReadBytes(31));
                p.VesselName = DecodeFixedNullTrim(enc, br.ReadBytes(31));
                p.DetPlatform = DecodeFixedNullTrim(enc, br.ReadBytes(31));
                p.SpeedLow = DecodeFixedNullTrim(enc, br.ReadBytes(31));
                p.SpeedHigh = DecodeFixedNullTrim(enc, br.ReadBytes(31));

                list.Add(p);
            }

            return list;
        }

        // =========================
        // Helpers (ClaFileReader 스타일)
        // =========================
        private static void SkipNewline(FileStream fs)
        {
            // \r\n 또는 \n 둘 다 처리
            int b1 = fs.ReadByte();
            if (b1 == -1) return;

            if (b1 != '\r')
                fs.Position--;

            int b2 = fs.ReadByte();
            if (b2 == -1) return;

            if (b2 != '\n')
                fs.Position--;
        }

        private static byte[] ReadExact(FileStream fs, int count)
        {
            var buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int n = fs.Read(buf, offset, count - offset);
                if (n <= 0) throw new EndOfStreamException($"Need {count} bytes, got {offset}.");
                offset += n;
            }
            return buf;
        }

        private static string Decode(Encoding enc, byte[] buf, int offset, int count)
        {
            // 고정폭 텍스트는 보통 스페이스 패딩 + 널이 섞일 수 있음
            var s = enc.GetString(buf, offset, count);
            return Clean(s);
        }

        private static string DecodeFixedNullTrim(Encoding enc, byte[] buf)
        {
            var s = enc.GetString(buf);
            return Clean(s);
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // C++ RemoveSpace의 정확한 동작은 미확정이지만, 고정폭 패딩 제거 목적은 Trim이 충족
            return s.Replace("\0", "").Trim();
        }

        private static int ParseInt(string s)
            => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        private static byte ParseByte(string s)
            => byte.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (byte)0;

        private static double ParseDouble(string s)
            => double.TryParse(s.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var v) ? v : 0.0;

        private static float ParseFloat(string s)
            => float.TryParse(s.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var v) ? v : 0.0f;
    }
}