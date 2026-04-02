using DBImplant_N1toN2.Infrastructure.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DBImplant_N1toN2.Features.NarrowBand.Models
{
    public static class NbAuxReaders
    {
        // =========================
        // .set Models
        // =========================
        public enum NbWindowKind
        {
            KaiserBessel = 0,
            Hamming = 1,
            Hanning = 2,
            Blackman = 3,
            Unknown = 99
        }

        public enum NbNoiseNormalizationKind
        {
            S3PM = 0,
            S2PM = 1,
            TPM = 2,
            OTA = 3,
            Unknown = 99
        }

        public sealed class NbVernierParameter
        {
            public double VernierBand { get; set; }
            public double CenterFreq { get; set; }
            public double StartFreq { get; set; }
            public double EndFreq { get; set; }
        }

        public sealed class NbDemonParameter
        {
            public double DemonBand { get; set; }
            public double CenterFreq { get; set; }
            public double StartFreq { get; set; }
            public double EndFreq { get; set; }
            public double DisplayBand { get; set; }
        }

        public sealed class NbParameterSet
        {
            // Preprocess
            public int SamplingFreq { get; set; }
            public int LofarBand { get; set; }

            public string WindowName { get; set; } = "";
            public NbWindowKind WindowKind { get; set; }

            public int KaiserBessel { get; set; }
            public int Overlap { get; set; }

            public int FftSizeFromFile { get; set; }
            public int FftSizeEffective { get; set; } // C++: 무조건 4096

            // Noise normalization
            public string NoiseNormalizationName { get; set; } = "";
            public NbNoiseNormalizationKind NoiseNormalizationKind { get; set; }

            public int WindowSize { get; set; }
            public int WindowGapSize { get; set; }
            public int ShearingThreshold { get; set; }

            // Integration
            public double LinearIntegrationTime { get; set; }
            public int LinearIntegrationNum { get; set; }
            public double ExpIntegrationTime { get; set; }
            public int ExpIntegrationNum { get; set; }

            // Bands (get-only는 그대로 OK)
            public List<NbVernierParameter> VernierParameters { get; } = new();
            public List<NbDemonParameter> DemonParameters { get; } = new();

            // Display
            public double AScale { get; set; }
            public double BScale { get; set; }
        }

       

        // =========================
        // Public APIs
        // =========================
        public static NbParameterSet ReadNbParameterSet(string filePath, Encoding? enc = null)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException(filePath);

            enc ??= Encoding.GetEncoding(949);
            using var sr = new StreamReader(filePath, enc, detectEncodingFromByteOrderMarks: true);
            using var tr = new TokenReader(sr);

            var p = new NbParameterSet();

            // 전처리
            p.SamplingFreq = tr.NextInt();
            p.LofarBand = tr.NextInt();

            // Window
            p.WindowName = tr.NextToken(); // KAISER-BESSEL
            p.WindowKind = ParseWindowKind(p.WindowName);

            p.KaiserBessel = tr.NextInt();
            p.Overlap = tr.NextInt();

            p.FftSizeFromFile = tr.NextInt();
            p.FftSizeEffective = 4096; // C++처럼 강제

            // Noise normalization
            p.NoiseNormalizationName = tr.NextToken(); // S3PM
            p.NoiseNormalizationKind = ParseNoiseNormKind(p.NoiseNormalizationName);

            p.WindowSize = tr.NextInt();
            p.WindowGapSize = tr.NextInt();
            p.ShearingThreshold = tr.NextInt();

            p.LinearIntegrationTime = tr.NextDouble();
            p.LinearIntegrationNum = tr.NextInt();

            p.ExpIntegrationTime = tr.NextDouble();
            p.ExpIntegrationNum = tr.NextInt();

            // 여기부터: 남은 토큰 전부 읽고 밴드 수 추정
            var rest = new List<string>();
            while (true)
            {
                try { rest.Add(tr.NextToken()); }
                catch (EndOfStreamException) { break; }
            }

            if (rest.Count < 2)
                throw new FormatException(".set: missing AScale/BScale tokens.");

            // 마지막 2개가 A/B
            p.AScale = double.TryParse(rest[^2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) ? a : 0.0;
            p.BScale = double.TryParse(rest[^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : 0.0;

            var bandTokens = rest.Take(rest.Count - 2).ToList();
            if (bandTokens.Count % 9 != 0)
                throw new FormatException($".set: remaining token count({bandTokens.Count}) not divisible by 9 (4+5 per band).");

            int bandCount = bandTokens.Count / 9;
            int idx = 0;

            p.VernierParameters.Clear();
            for (int i = 0; i < bandCount; i++)
            {
                p.VernierParameters.Add(new NbVernierParameter
                {
                    VernierBand = ParseD(bandTokens[idx++]),
                    CenterFreq = ParseD(bandTokens[idx++]),
                    StartFreq = ParseD(bandTokens[idx++]),
                    EndFreq = ParseD(bandTokens[idx++]),
                });
            }

            p.DemonParameters.Clear();
            for (int i = 0; i < bandCount; i++)
            {
                p.DemonParameters.Add(new NbDemonParameter
                {
                    DemonBand = ParseD(bandTokens[idx++]),
                    CenterFreq = ParseD(bandTokens[idx++]),
                    StartFreq = ParseD(bandTokens[idx++]),
                    EndFreq = ParseD(bandTokens[idx++]),
                    DisplayBand = ParseD(bandTokens[idx++]),
                });
            }

            return p;

            static double ParseD(string s)
                => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.0;
        }

        

        // =========================
        // Helpers
        // =========================
        private static string Clean(string s)
            => (s ?? "").Replace("\0", "").Trim();

        private static bool TryParseInt(string s, out int v)
            => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

        private static bool TryParseDouble(string s, out double v)
            => double.TryParse(s.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out v);

        private static NbWindowKind ParseWindowKind(string s)
        {
            var x = s.Trim();
            return x switch
            {
                "KAISER-BESSEL" => NbWindowKind.KaiserBessel,
                "HAMMING" => NbWindowKind.Hamming,
                "HANNING" => NbWindowKind.Hanning,
                "BLACKMAN" => NbWindowKind.Blackman,
                _ => NbWindowKind.Unknown
            };
        }

        private static NbNoiseNormalizationKind ParseNoiseNormKind(string s)
        {
            var x = s.Trim();
            return x switch
            {
                "S3PM" => NbNoiseNormalizationKind.S3PM,
                "S2PM" => NbNoiseNormalizationKind.S2PM,
                "TPM" => NbNoiseNormalizationKind.TPM,
                "OTA" => NbNoiseNormalizationKind.OTA,
                _ => NbNoiseNormalizationKind.Unknown
            };
        }
    }
}