using DBImplant_N1toN2.Features.NarrowBand.Models;
using DBImplant_N1toN2.Features.Supplement;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using static DBImplant_N1toN2.Features.NarrowBand.Models.NbAuxReaders;
using static DBImplant_N1toN2.Features.NarrowBand.Parsers.NbResultReader;
using static DBImplant_N1toN2.Features.Supplement.AlgReader;

namespace DBImplant_N1toN2.Features.NarrowBand.Parsers
{
    public class NbLoaded
    {
        public string NbDir { get; init; } = "";
        public NbLoadReport Report { get; init; } = new();

        // 루트 파일 경로들
        public string? SetPath { get; set; }
        public byte[]? SetBytes { get; set; }
        public NbParameterSet? ParsedSet { get; set; }

        public string? SignalWavPath { get; set; }
        public string? LofarGrmPath { get; set; }
        public string? DemonGrmPath { get; set; }
        public string? Demon2GrmPath { get; set; }
        public string? Demon3GrmPath { get; set; }
        public string? Demon4GrmPath { get; set; }
        public string? VernierGrmPath { get; set; }
        public string? Vernier2GrmPath { get; set; }
        public string? Vernier3GrmPath { get; set; }
        public string? Vernier4GrmPath { get; set; }

        // 정밀분석 관련 파일 경로들
        public string? FeaPath { get; set; }
        public byte[]? FeaBytes { get; set; }
        public NbFeatureInfo? ParsedFea { get; set; }

        public string? DatPath { get; set; }
        public byte[]? DatBytes { get; set; }
        public List<ScoringParam>? ParsedDat { get; set; }

        public string? ClaPath { get; set; }
        public byte[]? ClaBytes { get; set; }
        public NbClassificationResult? ParsedCla { get; set; }

        // 스니펫 관련 파일 경로들
        public List<SnippetPair> Snippets { get; set; } = new();
        public List<SnippetPair> TmpSnippets { get; set; } = new();

        // 에러 메시지들
        public string? ParsedSetError { get; set; }
        public string? ParsedFeaError { get; set; }
        public string? ParsedDatError { get; set; }
        public string? ParsedClaError { get; set; }

        // ===== .alg(로그시트) =====
        public string? AlgPath { get; set; }
        public byte[]? AlgBytes { get; set; }
        public SupplementSheet? ParsedAlg { get; set; }

        public string? ParsedAlgError { get; set; }
        public void ParseFiles()
        {
            // 파일 파싱 수행
            try
            {
                // .set 파일 파싱
                if (SetPath != null)
                {
                    SetBytes = File.ReadAllBytes(SetPath);
                    ParsedSet = NbAuxReaders.ReadNbParameterSet(SetPath);
                }

                // .fea 파일 파싱
                if (FeaPath != null)
                {
                    FeaBytes = File.ReadAllBytes(FeaPath);
                    ParsedFea = NbResultReader.ReadNbFeatureInfoFea(FeaPath);
                }

                // .dat 파일 파싱
                if (DatPath != null)
                {
                    DatBytes = File.ReadAllBytes(DatPath);
                    ParsedDat = NbResultReader.ReadScoringParamDat(DatPath);
                }

                // .cla 파일 파싱
                if (ClaPath != null)
                {
                    ClaBytes = File.ReadAllBytes(ClaPath);
                    ParsedCla = NbResultReader.ReadNbClassificationCla(ClaPath);
                }

                // .alg
                if (AlgPath != null)
                {
                    try
                    {
                        AlgBytes = File.ReadAllBytes(AlgPath);
                        ParsedAlg = ReadSupplementAlg(AlgPath);
                    }
                    catch (Exception ex)
                    {
                        ParsedAlgError = ex.Message;
                        Report.MissingOptional.Add($".alg 파싱 실패: {AlgPath} / {ex.Message}");
                    }
                }

                // Snippets 및 TmpSnippets 폴더 파싱
                string snippetsDir = Path.Combine(NbDir, "정밀분석", "Snippets");
                if (Directory.Exists(snippetsDir))
                {
                    ScanSnippetPairs(snippetsDir, Report, Snippets);
                }

                string snpTmpDir = Path.Combine(NbDir, "정밀분석", "SnpTmp");
                if (Directory.Exists(snpTmpDir))
                {
                    ScanSnippetPairs(snpTmpDir, Report, TmpSnippets);
                }

            }
            catch (Exception ex)
            {
                // 에러 발생 시 처리
                Report.MissingRequired.Add($"파일 파싱 실패: {ex.Message}");
            }
        }

        // 스니펫 쌍을 읽어오는 메소드
        private void ScanSnippetPairs(string dir, NbLoadReport report, List<SnippetPair> dst)
        {
            var wav = Directory.EnumerateFiles(dir, "*.wav", SearchOption.TopDirectoryOnly);
            var grm = Directory.EnumerateFiles(dir, "*.grm", SearchOption.TopDirectoryOnly);

            var wavDict = wav.ToDictionary(
                p => Path.GetFileNameWithoutExtension(p),
                p => p,
                StringComparer.OrdinalIgnoreCase);

            var grmDict = grm.ToDictionary(
                p => Path.GetFileNameWithoutExtension(p),
                p => p,
                StringComparer.OrdinalIgnoreCase);

            var allBase = new HashSet<string>(wavDict.Keys, StringComparer.OrdinalIgnoreCase);
            allBase.UnionWith(grmDict.Keys);

            foreach (var b in allBase.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                wavDict.TryGetValue(b, out var w);
                grmDict.TryGetValue(b, out var g);

                var pair = new SnippetPair { BaseName = b, WavPath = w, GrmPath = g };
                dst.Add(pair);

                if (!pair.IsComplete)
                {
                    report.PairMismatches.Add($"[{Path.GetFileName(dir)}] 페어 불일치: {b} (wav={(w != null)}, grm={(g != null)})");
                }
            }

            Debug.WriteLine($"[NB] ScanSnippetPairs {dir} -> {dst.Count} pairs, mismatch={report.PairMismatches.Count}");
        }
    }

    public class NbLoadReport
    {
        public List<string> MissingRequired { get; } = new();
        public List<string> MissingConditional { get; } = new();
        public List<string> MissingOptional { get; } = new();
        public List<string> PairMismatches { get; } = new();

        public bool Ok =>
            MissingRequired.Count == 0 &&
            MissingConditional.Count == 0 &&
            PairMismatches.Count == 0;

        public string BuildMessage()
        {
            static string Join(List<string> xs) => xs.Count == 0 ? "(none)" : string.Join(Environment.NewLine, xs);

            return
                "NB 패키지 검증 실패\n" +
                $"[MissingRequired]\n{Join(MissingRequired)}\n\n" +
                $"[MissingConditional]\n{Join(MissingConditional)}\n\n" +
                $"[MissingOptional]\n{Join(MissingOptional)}\n\n" +
                $"[PairMismatches]\n{Join(PairMismatches)}\n";
        }
    }

    // 스니펫 페어 클래스
    public sealed class SnippetPair
    {
        public string BaseName { get; set; } = "";       // 확장자 제외
        public string? WavPath { get; set; }
        public string? GrmPath { get; set; }

        public bool IsComplete => !string.IsNullOrWhiteSpace(WavPath) && !string.IsNullOrWhiteSpace(GrmPath);
    }



    public enum Bucket
    {
        Required,   // 필수 파일
        Conditional, // 조건부 파일
        Optional    // 선택적 파일
    }

    public static class NbLoader
    {
        /// <summary>
        /// *.nb 패키지(협대역 원음자료) 로드:
        /// - 1) 파일/폴더 존재 검증
        /// - 2) .fea/.cla/.dat/.set 은 옵션에 따라 byte[] 로 읽어둠
        /// - 3) .wav/.grm 은 기본 경로만 보관(필요하면 옵션으로 bytes 로드 가능하게 확장 가능)
        /// </summary>
        public static NbLoaded Load(string nbDir)
        {
            var report = new NbLoadReport();

            if (!Directory.Exists(nbDir))
            {
                report.MissingRequired.Add($"NB 폴더 없음: {nbDir}");
                return new NbLoaded { NbDir = nbDir, Report = report };
            }

            // 루트 파일 찾기 및 로드
            var nbLoaded = new NbLoaded { NbDir = nbDir };

            nbLoaded.SetPath = FindFileByNames(nbDir, "정밀신호처리파라미터.set");
            if (nbLoaded.SetPath != null)
                nbLoaded.SetBytes = File.ReadAllBytes(nbLoaded.SetPath);

            nbLoaded.SignalWavPath = FindFileByNames(nbDir, "Signal.wav", "Singal.wav");

            // .grm 파일들 로드
            nbLoaded.LofarGrmPath = FindFileByNames(nbDir, "LOFAR.grm");
            nbLoaded.DemonGrmPath = FindFileByNames(nbDir, "DEMON.grm");
            nbLoaded.Demon2GrmPath = FindFileByNames(nbDir, "DEMON2.grm");
            nbLoaded.Demon3GrmPath = FindFileByNames(nbDir, "DEMON3.grm");
            nbLoaded.Demon4GrmPath = FindFileByNames(nbDir, "DEMON4.grm");
            nbLoaded.VernierGrmPath = FindFileByNames(nbDir, "VERNIER.grm");

            // 정밀분석 폴더가 있으면 추가 파싱
            var PreciseDir = Path.Combine(nbDir, "정밀분석");
            if (Directory.Exists(PreciseDir))
            {
                nbLoaded.FeaPath = FindFileByNames(PreciseDir, "nb_식별결과.fea");
                nbLoaded.DatPath = FindFileByNames(PreciseDir, "식별파라미터.dat");
                nbLoaded.ClaPath = FindFileByNames(PreciseDir, "nb_식별결과.cla");
                nbLoaded.AlgPath = FindFileByNames(PreciseDir, "보조정보.alg");

                nbLoaded.ParseFiles();
            }

            return nbLoaded;
        }

       

        private static string? FindFileByNames(string dir, params string[] names)
        {
            if (!Directory.Exists(dir)) return null;

            var files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly);

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                foreach (var name in names)
                {
                    if (fileName.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return file;
                }
            }
            return null;
        }

    }
}