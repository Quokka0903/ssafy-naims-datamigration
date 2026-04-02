using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DBImplant_N1toN2.DataAccess;
using DBImplant_N1toN2.Features.FileBrowser;

namespace DBImplant_N1toN2.Features.Migration.Models
{
    public static class MigrationMappingRegistry
    {
        // ── DB 컬럼 매핑 미리보기 ─────────────────────────────────────────

        public static List<MigrationPreviewRow> BuildDbPreview(
            ILegacyDbConnection legacyDb, FileBrowserViewModel fileBrowser)
        {
            var rows = new List<MigrationPreviewRow>();

            // ── PREC_ANALYSIS (SsParam 파싱 데이터 기반)
            rows.Add(R("PREC_ANALYSIS", "ANALYSIS_ID",    "Computed", "AutoIncrement",              "1",                   "BIGINT",   "PK, IDENTITY"));
            rows.Add(R("PREC_ANALYSIS", "PRECISE_NAME",   "File",     "패키지 폴더명",               "202501_임무A",         "NVARCHAR(200)", "SD 패키지 폴더명"));
            rows.Add(R("PREC_ANALYSIS", "DELETED",         "Constant", "0",                          "0",                   "BIT",      "정책: DELETED=0 고정"));
            rows.Add(R("PREC_ANALYSIS", "IS_TRAINING",     "Constant", "0",                          "0",                   "BIT",      "정책: IS_TRAINING=0 고정"));

            // ── PREC_SS_PARAM
            rows.Add(R("PREC_SS_PARAM", "PARAM_ID",        "Computed", "AutoIncrement",              "1",                   "BIGINT",   "PK, IDENTITY"));
            rows.Add(R("PREC_SS_PARAM", "ANALYSIS_ID",     "Computed", "FK→PREC_ANALYSIS",           "1",                   "BIGINT",   "FK"));
            rows.Add(R("PREC_SS_PARAM", "DT_THRESHOLD",    "File",     "정밀분석설정.set→DtThreshold", "2.5",                 "FLOAT",    ""));
            rows.Add(R("PREC_SS_PARAM", "BANDWIDTH",       "File",     "정밀분석설정.set→Bandwidth",   "10.0",                "FLOAT",    ""));

            // ── PREC_SS_EVENT
            rows.Add(R("PREC_SS_EVENT", "EVENT_ID",        "Computed", "AutoIncrement",              "1",                   "BIGINT",   "PK, IDENTITY"));
            rows.Add(R("PREC_SS_EVENT", "ANALYSIS_ID",     "Computed", "FK→PREC_ANALYSIS",           "1",                   "BIGINT",   "FK"));
            rows.Add(R("PREC_SS_EVENT", "START_TIME",       "File",     "이벤트리스트.lst→StartTime",  "2025-01-15 03:22:11", "DATETIME2",""));
            rows.Add(R("PREC_SS_EVENT", "END_TIME",         "File",     "이벤트리스트.lst→EndTime",    "2025-01-15 03:23:45", "DATETIME2",""));
            rows.Add(R("PREC_SS_EVENT", "DETECT_TYPE",      "File",     "이벤트리스트.lst→DetectType", "AI",                  "VARCHAR(10)","AI/TR/UNK"));
            rows.Add(R("PREC_SS_EVENT", "EVENT_WAV_PATH",   "File",     "Signal_*.wav → EventID",     "Signal_001.wav",      "NVARCHAR(500)","EventID=파일명 접미사"));

            // ── PREC_SS_SEGMENT
            rows.Add(R("PREC_SS_SEGMENT", "SEGMENT_ID",    "Computed", "AutoIncrement",              "1",                   "BIGINT",   "PK, IDENTITY"));
            rows.Add(R("PREC_SS_SEGMENT", "EVENT_ID",      "Computed", "FK→PREC_SS_EVENT",           "1",                   "BIGINT",   "FK"));
            rows.Add(R("PREC_SS_SEGMENT", "FREQ_START",    "File",     "SPSlist→FreqStart",           "100.0",               "FLOAT",    ""));
            rows.Add(R("PREC_SS_SEGMENT", "FREQ_END",      "File",     "SPSlist→FreqEnd",             "200.0",               "FLOAT",    ""));
            rows.Add(R("PREC_SS_SEGMENT", "MODULATION",    "File",     "SPSlist→Modulation",          "3.5",                 "FLOAT",    ""));

            // ── PREC_SS_FRAME
            rows.Add(R("PREC_SS_FRAME", "FRAME_ID",        "Computed", "AutoIncrement",              "1",                   "BIGINT",   "PK, IDENTITY"));
            rows.Add(R("PREC_SS_FRAME", "SEGMENT_ID",      "Computed", "FK→PREC_SS_SEGMENT",         "1",                   "BIGINT",   "FK"));
            rows.Add(R("PREC_SS_FRAME", "MFB_VALUE",       "File",     "SPSlist→MFB or tr.tmp",       "(binary)",            "VARBINARY","MFB 프레임 데이터"));

            // ── RAW_COLLECT_SIG (수집원음자료 폴더 → 1폴더=1레코드)
            rows.Add(R("RAW_COLLECT_SIG", "COLLECT_SIG_ID",  "Computed", "AutoIncrement",              "1",                   "BIGINT",   "PK, IDENTITY"));
            rows.Add(R("RAW_COLLECT_SIG", "MISSION_NAME",    "File",     "수집원음자료/임무폴더명",       "202501_임무A",         "NVARCHAR(200)","폴더명=임무명"));
            rows.Add(R("RAW_COLLECT_SIG", "FOLDER_PATH",     "File",     "수집원음자료/임무폴더 경로",    "D:\\MSU\\수집원음...", "NVARCHAR(500)",""));

            // ── RAW_SCHANNEL_INFO (수집원음 > 협대역 > WAV 1개당 1건)
            rows.Add(R("RAW_SCHANNEL_INFO","SCHANNEL_ID",    "Computed", "AutoIncrement",              "1",                   "BIGINT",   "PK, IDENTITY"));
            rows.Add(R("RAW_SCHANNEL_INFO","COLLECT_SIG_ID", "Computed", "FK→RAW_COLLECT_SIG",         "1",                   "BIGINT",   "FK"));
            rows.Add(R("RAW_SCHANNEL_INFO","WAV_FILENAME",   "File",     "협대역/*.wav 파일명",          "ch01_2025.wav",       "NVARCHAR(200)",""));
            rows.Add(R("RAW_SCHANNEL_INFO","FILE_SIZE",      "File",     "파일 크기",                   "1048576",             "BIGINT",   "바이트"));

            // ── RAW_SCHANNEL_NB_INFO (wav와 같은 이름의 .grm/.dat/.txt)
            rows.Add(R("RAW_SCHANNEL_NB_INFO","NB_INFO_ID",  "Computed", "AutoIncrement",              "1",                   "BIGINT",   "PK, IDENTITY"));
            rows.Add(R("RAW_SCHANNEL_NB_INFO","SCHANNEL_ID", "Computed", "FK→RAW_SCHANNEL_INFO",       "1",                   "BIGINT",   "FK"));
            rows.Add(R("RAW_SCHANNEL_NB_INFO","FILE_NAME",   "File",     "협대역/xxx.grm|dat|txt",      "ch01_2025.grm",       "NVARCHAR(200)","wav 이름 기준 그룹"));
            rows.Add(R("RAW_SCHANNEL_NB_INFO","FILE_TYPE",   "File",     "확장자",                      ".GRM",                "VARCHAR(10)","GRM/DAT/TXT"));

            // ── PREC_SRC_SIG (협대역/단주기 원음자료 → 1폴더=1레코드)
            rows.Add(R("PREC_SRC_SIG", "SRC_SIG_ID",        "Computed", "AutoIncrement",              "1",                   "BIGINT",   "PK, IDENTITY"));
            rows.Add(R("PREC_SRC_SIG", "FOLDER_TYPE",       "Computed", "'SD' or 'NB'",               "SD",                  "VARCHAR(10)","단주기/협대역 구분"));
            rows.Add(R("PREC_SRC_SIG", "FOLDER_NAME",       "File",     "패키지 폴더명",               "202501_임무A.sd",      "NVARCHAR(200)",""));

            // ── VESSEL_DATA (레거시 DB 직접 이관)
            rows.Add(R("VESSEL_DATA",  "VESSEL_ID",        "LegacyDB", "VESSEL_DATA.VESSEL_ID",      "1001",                "INT",      "레거시 DB에서 직접 이관"));
            rows.Add(R("VESSEL_DATA",  "VESSEL_NAME",      "LegacyDB", "VESSEL_DATA.VESSEL_NAME",    "(CRYPTO.DECRYPT)",    "NVARCHAR(31)","Oracle:DECRYPT필요"));
            rows.Add(R("VESSEL_DATA",  "CLASS_ID",         "LegacyDB", "VESSEL_DATA.CLASS_ID",       "5",                   "INT",      "FK→CLASSES"));

            // ── SIGNATURES (레거시 DB)
            rows.Add(R("SIGNATURES",   "SIGNATURE_ID",     "LegacyDB", "SIGNATURES.SIGNATURE_ID",    "2001",                "INT",      "레거시 DB에서 직접 이관"));
            rows.Add(R("SIGNATURES",   "VESSEL_ID",        "LegacyDB", "SIGNATURES.VESSEL_ID",       "1001",                "INT",      "FK→VESSEL_DATA"));
            rows.Add(R("SIGNATURES",   "FREQ_NOISE_AVG_HZ","LegacyDB", "SIGNATURES.FREQ_NOISE_AVG",  "125.5",               "FLOAT",    ""));

            return rows;
        }

        // ── 파일트리 이동 매핑 미리보기 ───────────────────────────────────

        public static List<FileTransferPreviewRow> BuildFilePreview(FileBrowserViewModel fileBrowser)
        {
            var rows = new List<FileTransferPreviewRow>();

            // SD 패키지 파일 매핑
            AddSdFileRules(rows, fileBrowser);

            // NB 패키지 파일 매핑
            AddNbFileRules(rows, fileBrowser);

            // Raw 패키지 파일 매핑
            AddRawFileRules(rows, fileBrowser);

            return rows;
        }

        // ── SD 파일 매핑 ─────────────────────────────────────────────────

        private static void AddSdFileRules(List<FileTransferPreviewRow> rows, FileBrowserViewModel fb)
        {
            var sdPath = Models.AppSettingsHelper.GetShortPeriodRaw();
            if (!Directory.Exists(sdPath)) return;

            foreach (var pkgDir in SafeGetDirectories(sdPath, "*.sd"))
            {
                var pkg = Path.GetFileName(pkgDir);
                AddSdPackageFiles(rows, pkg, pkgDir);
            }

            // 패키지가 없으면 기본 규칙 행 추가 (어떤 파일이 어떻게 매핑되는지)
            if (!rows.Any(r => r.Category == "단주기(SD)"))
            {
                rows.Add(F("단주기(SD)", "(패키지)", "Signal_*.wav",          "WAV", "PREC_SS_EVENT",    "경로참조",     0, "EventID = 파일명 접미사 숫자"));
                rows.Add(F("단주기(SD)", "(패키지)", "SPSlist__.txt",         "SPS", "PREC_SS_SEGMENT",  "파싱→INSERT",  0, "바이너리 64B 고정레코드"));
                rows.Add(F("단주기(SD)", "(패키지)", "이벤트리스트.lst",       "LST", "PREC_SS_EVENT",    "파싱→INSERT",  0, "EUC-KR 고정폭 텍스트"));
                rows.Add(F("단주기(SD)", "(패키지)", "정밀분석설정.set",       "SET", "PREC_SS_PARAM",    "파싱→INSERT",  0, "분석 파라미터"));
                rows.Add(F("단주기(SD)", "(패키지)", "정밀분석/AI/ai_식별결과.cla","CLA","PREC_SS_EVENT",  "파싱→INSERT",  0, "AI 분류 결과 (고정폭)"));
                rows.Add(F("단주기(SD)", "(패키지)", "정밀분석/AI/ai_식별결과.tmp","TMP","PREC_SS_SEGMENT","파싱→INSERT",  0, "AI 바이너리 결과 (24B)"));
                rows.Add(F("단주기(SD)", "(패키지)", "정밀분석/TR/tr_식별결과.cla","CLA","PREC_SS_EVENT",  "파싱→INSERT",  0, "TR 분류 결과 (고정폭)"));
                rows.Add(F("단주기(SD)", "(패키지)", "정밀분석/TR/tr_식별결과.tmp","TMP","PREC_SS_FRAME",  "파싱→INSERT",  0, "TR 바이너리 결과 (636B)"));
                rows.Add(F("단주기(SD)", "(패키지)", "정밀분석/AI/Snippets/*.wav","WAV","PREC_SS_EVENT",   "BLOB→INSERT",  0, "AI 스니펫 WAV"));
                rows.Add(F("단주기(SD)", "(패키지)", "정밀분석/AI/Snippets/*.grm","GRM","—",               "스킵(Phase2)",0, "GRM 변환 스펙 미확정"));
            }
        }

        private static void AddSdPackageFiles(List<FileTransferPreviewRow> rows, string pkg, string pkgDir)
        {
            foreach (var file in SafeGetFiles(pkgDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(pkgDir, file);
                var ext = Path.GetExtension(file).ToUpperInvariant().TrimStart('.');
                var size = new FileInfo(file).Length;

                var (target, action, note) = ClassifySdFile(rel, ext);
                rows.Add(F("단주기(SD)", pkg, rel, ext, target, action, size, note));
            }
        }

        private static (string target, string action, string note) ClassifySdFile(string rel, string ext)
        {
            var lower = rel.Replace('\\', '/').ToLowerInvariant();

            if (ext == "WAV" && lower.StartsWith("signal_"))
                return ("PREC_SS_EVENT", "경로참조", "EventID = 파일명 접미사");
            if (ext == "WAV" && lower.Contains("snippet"))
                return ("PREC_SS_EVENT", "BLOB→INSERT", "스니펫 WAV");
            if (lower.Contains("spslist"))
                return ("PREC_SS_SEGMENT", "파싱→INSERT", "SPS 바이너리");
            if (lower.Contains("이벤트리스트") && ext == "LST")
                return ("PREC_SS_EVENT", "파싱→INSERT", "이벤트 리스트");
            if (lower.Contains("탐지리스트") && ext == "LST")
                return ("PREC_SS_EVENT", "파싱→INSERT", "탐지 리스트 (AI/TR)");
            if (lower.Contains("정밀분석설정") && ext == "SET")
                return ("PREC_SS_PARAM", "파싱→INSERT", "분석 파라미터");
            if (ext == "CLA")
                return ("PREC_SS_EVENT", "파싱→INSERT", "분류 결과");
            if (ext == "TMP")
                return ("PREC_SS_FRAME", "파싱→INSERT", "바이너리 결과");
            if (ext == "GRM")
                return ("—", "스킵(Phase2)", "GRM 변환 스펙 미확정");

            return ("—", "스킵", "매핑 대상 아님");
        }

        // ── NB 파일 매핑 ─────────────────────────────────────────────────

        private static void AddNbFileRules(List<FileTransferPreviewRow> rows, FileBrowserViewModel fb)
        {
            var nbPath = Models.AppSettingsHelper.GetNarrowbandRaw();
            if (!Directory.Exists(nbPath)) return;

            var nbDirs = SafeGetDirectories(nbPath, "*").Take(5); // 미리보기: 최대 5개 임무
            foreach (var missionDir in nbDirs)
            {
                var mission = Path.GetFileName(missionDir);
                foreach (var file in SafeGetFiles(missionDir, "*", SearchOption.AllDirectories).Take(20))
                {
                    var rel = Path.GetRelativePath(missionDir, file);
                    var ext = Path.GetExtension(file).ToUpperInvariant().TrimStart('.');
                    var size = new FileInfo(file).Length;
                    var (target, action, note) = ClassifyNbFile(rel, ext);
                    rows.Add(F("협대역(NB)", mission, rel, ext, target, action, size, note));
                }
            }

            if (!rows.Any(r => r.Category == "협대역(NB)"))
            {
                rows.Add(F("협대역(NB)", "(임무)", "nb_식별결과.fea", "FEA", "NB_SNIPPET", "파싱→INSERT", 0, "주파수 특징 정보"));
                rows.Add(F("협대역(NB)", "(임무)", "식별파라미터.dat", "DAT", "NB_SNIPPET", "파싱→INSERT", 0, "식별 파라미터"));
                rows.Add(F("협대역(NB)", "(임무)", "nb_식별결과.cla", "CLA", "NB_SNIPPET", "파싱→INSERT", 0, "분류 결과"));
                rows.Add(F("협대역(NB)", "(임무)", "Snippets/*.wav",  "WAV", "NB_SNIPPET", "BLOB→INSERT", 0, "협대역 스니펫"));
                rows.Add(F("협대역(NB)", "(임무)", "Snippets/*.grm",  "GRM", "—",          "스킵(Phase2)",0, "GRM 변환 스펙 미확정"));
            }
        }

        private static (string target, string action, string note) ClassifyNbFile(string rel, string ext)
        {
            if (ext == "FEA") return ("NB_SNIPPET", "파싱→INSERT", "주파수 특징");
            if (ext == "DAT") return ("NB_SNIPPET", "파싱→INSERT", "식별 파라미터");
            if (ext == "CLA") return ("NB_SNIPPET", "파싱→INSERT", "분류 결과");
            if (ext == "WAV") return ("NB_SNIPPET", "BLOB→INSERT", "스니펫 WAV");
            if (ext == "GRM") return ("—", "스킵(Phase2)", "GRM 변환 미확정");
            return ("—", "스킵", "");
        }

        // ── Raw 파일 매핑 ────────────────────────────────────────────────

        private static void AddRawFileRules(List<FileTransferPreviewRow> rows, FileBrowserViewModel fb)
        {
            var rawPath = Models.AppSettingsHelper.GetCollectRaw();
            if (!Directory.Exists(rawPath)) return;

            var rawDirs = SafeGetDirectories(rawPath, "*").Take(3);
            foreach (var missionDir in rawDirs)
            {
                var mission = Path.GetFileName(missionDir);
                foreach (var file in SafeGetFiles(missionDir, "*", SearchOption.TopDirectoryOnly).Take(10))
                {
                    var rel = Path.GetFileName(file);
                    var ext = Path.GetExtension(file).ToUpperInvariant().TrimStart('.');
                    var size = new FileInfo(file).Length;
                    var (target, action, note) = ClassifyRawFile(rel, ext);
                    rows.Add(F("수집원음(Raw)", mission, rel, ext, target, action, size, note));
                }
            }

            if (!rows.Any(r => r.Category == "수집원음(Raw)"))
            {
                rows.Add(F("수집원음(Raw)", "(임무)", "Signal_*.wav", "WAV", "PREC_ANALYSIS", "경로참조", 0, "원음 WAV 참조"));
            }
        }

        private static (string target, string action, string note) ClassifyRawFile(string rel, string ext)
        {
            if (ext == "WAV") return ("PREC_ANALYSIS", "경로참조", "원음 WAV");
            return ("—", "스킵", "");
        }

        // ── 유틸 ─────────────────────────────────────────────────────────

        private static MigrationPreviewRow R(
            string table, string column, string srcType, string srcPath,
            string sample, string sqlType, string note)
            => new()
            {
                TargetTable = table, TargetColumn = column, SourceType = srcType,
                SourcePath = srcPath, SampleValue = sample, SqlDataType = sqlType, Note = note,
            };

        private static FileTransferPreviewRow F(
            string cat, string pkg, string src, string fileType,
            string target, string action, long size, string note)
            => new()
            {
                Category = cat, PackageName = pkg, SourceFile = src, FileType = fileType,
                TargetTable = target, TargetAction = action, FileSize = size, Note = note,
            };

        private static string[] SafeGetDirectories(string path, string pattern)
        {
            try { return Directory.GetDirectories(path, pattern); }
            catch { return Array.Empty<string>(); }
        }

        private static string[] SafeGetFiles(string path, string pattern, SearchOption opt = SearchOption.TopDirectoryOnly)
        {
            try { return Directory.GetFiles(path, pattern, opt); }
            catch { return Array.Empty<string>(); }
        }
    }

    /// <summary>AppSettings.Roots의 경로를 정적으로 참조하는 헬퍼</summary>
    internal static class AppSettingsHelper
    {
        public static string GetShortPeriodRaw() => DBImplant_N1toN2.Models.AppSettings.Roots.ShortPeriodRaw;
        public static string GetNarrowbandRaw() => DBImplant_N1toN2.Models.AppSettings.Roots.NarrowbandRaw;
        public static string GetCollectRaw() => DBImplant_N1toN2.Models.AppSettings.Roots.CollectRaw;
    }
}
