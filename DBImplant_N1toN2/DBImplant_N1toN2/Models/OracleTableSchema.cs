using System;
using System.Collections.Generic;

namespace DBImplant_N1toN2.Models
{
    /// <summary>
    /// Oracle 레거시 DB (USPASK CDMU) 테이블 스키마 정의.
    /// TableManager_*.h 구조체 분석 기반 — 36개 테이블, CRYPTO 암호화 필드 표시.
    /// </summary>
    public static class OracleTableSchema
    {
        // ── CRYPTO.ENCRYPT/DECRYPT 대상 필드 ──────────────────────────────────────
        // Oracle 레거시에서 이름 필드들은 CRYPTO PL/SQL 패키지로 암호화됨.
        // 읽기: SELECT CRYPTO.DECRYPT(col) / 쓰기: INSERT ... CRYPTO.ENCRYPT(?)
        public static readonly Dictionary<string, string[]> CryptoFields = new()
        {
            ["CLASSES"]       = new[] { "CLASS_NAME", "CLASS_NAME_K", "CLASS_NAME_ISUS90" },
            ["VESSEL_DATA"]   = new[] { "VESSEL_NAME", "VESSEL_NAME_K", "VESSEL_NAME_ISUS90" },
            ["ACTIVE_SONAR"]  = new[] { "SONAR_NAME", "SONAR_NAME_ISUS90" },
            ["TRANSIENT"]     = new[] { "TR_NAME" },
        };

        // ── Oracle ↔ MsSQL 테이블명 대응 ─────────────────────────────────────────
        // Oracle 소스에서 다른 이름을 쓰는 테이블들
        public static readonly Dictionary<string, string> OracleToMssqlTableName = new()
        {
            ["TRANSIENT"]               = "TRANSIENT",      // TREvent cpp → TRANSIENT 테이블
            ["DB_DIST_LOG"]             = "DB_DIST_LOG",
            ["DB_DIST_GROUP"]           = "DB_DIST_GROUP",
            ["DISTRIB_INFO_GROUP"]      = "DISTRIB_INFO_GROUP", // 뷰 or 별칭
            ["PULSE_FREQUENCY_ISUS90"]  = "PULSE_FREQUENCY_ISUS90",
            ["PULSE_SEG_LENGTH_ISUS90"] = "PULSE_SEG_LENGTH_ISUS90",
            ["PULSE_SEG_SWING_ISUS90"]  = "PULSE_SEG_SWING_ISUS90",
            ["FRAME_SNIPPET"]           = "FRAME_SNIPPET",  // TRFrame_Snippet
        };

        // ── FK 관계 전체 맵 (Oracle 기준) ────────────────────────────────────────
        // 부모테이블 → (부모PK컬럼, 자식테이블, 자식FK컬럼)[]
        public static readonly Dictionary<string, (string Pk, string Child, string ChildFk)[]> FkRelations = new()
        {
            ["COUNTRY_CODES"] = new[]
            {
                ("COUNTRY_ID", "VESSEL_DATA", "COUNTRY_ID"),
            },
            ["CLASSES"] = new[]
            {
                ("CLASS_ID", "VESSEL_DATA",     "CLASS_ID"),
                ("CLASS_ID", "WEAPON_CARRIER",  "CLASS_ID"),
            },
            ["VESSEL_DATA"] = new[]
            {
                ("VESSEL_ID", "SIGNATURES",      "VESSEL_ID"),
                ("VESSEL_ID", "IMAGE_DATA",      "VESSEL_ID"),
                ("VESSEL_ID", "CHARACTERISTICS", "VESSEL_ID"),
                ("VESSEL_ID", "TRANSIENT",       "VESSEL_ID"),
            },
            ["SIGNATURES"] = new[]
            {
                ("SIGNATURE_ID", "HARMONICS",    "SIGNATURES_ID"),
                ("SIGNATURE_ID", "SIG_SNIPPET",  "SIGNATURE_ID"),
            },
            ["SIG_SNIPPET"] = new[]
            {
                ("NB_SNIPPET_ID", "NB_SNIPPET", "NB_SNIPPET_ID"),
            },
            ["ACTIVE_SONAR"] = new[]
            {
                ("SONAR_ID", "PULSES",                   "SONAR_ID"),
                ("SONAR_ID", "PULSE_SEGMENTS",           "SONAR_ID"),
                ("SONAR_ID", "PULSE_FREQUENCY_ISUS90",   "SONAR_ID"),
            },
            ["PULSES"] = new[]
            {
                ("PULSE_ID", "AI_SNIPPET", "PULSE_ID"),
            },
            ["PULSE_FREQUENCY_ISUS90"] = new[]
            {
                ("PULSE_F_ID", "PULSE_SEG_SWING_ISUS90",  "PULSE_F_ID"),
                ("PULSE_F_ID", "PULSE_SEG_LENGTH_ISUS90", "PULSE_F_ID"),
            },
            ["TRANSIENT"] = new[]
            {
                ("TR_ID", "TR_FRAME", "TR_ID"),
            },
            ["TR_FRAME"] = new[]
            {
                ("FRAME_ID", "FRAME_SNIPPET", "FRAME_ID"),
            },
            ["FRAME_SNIPPET"] = new[]
            {
                ("TR_SNIPPET_ID", "TR_SNIPPET", "TR_SNIPPET_ID"),
            },
            ["WEAPON"] = new[]
            {
                ("WEAPON_ID", "WEAPON_CARRIER", "WEAPON_ID"),
            },
            ["DB_DIST_GROUP"] = new[]
            {
                ("GROUP_ID", "DIST_LIST", "GROUP_ID"),
            },
            ["DB_DIST_LOG"] = new[]
            {
                ("LOG_ID", "DIST_TO", "LOG_ID"),
            },
            // 리프 노드 (자식 없음)
            ["NB_SNIPPET"]              = Array.Empty<(string, string, string)>(),
            ["AI_SNIPPET"]              = Array.Empty<(string, string, string)>(),
            ["TR_SNIPPET"]              = Array.Empty<(string, string, string)>(),
            ["HARMONICS"]               = Array.Empty<(string, string, string)>(),
            ["IMAGE_DATA"]              = Array.Empty<(string, string, string)>(),
            ["CHARACTERISTICS"]         = Array.Empty<(string, string, string)>(),
            ["PULSE_SEGMENTS"]          = Array.Empty<(string, string, string)>(),
            ["PULSE_SEG_SWING_ISUS90"]  = Array.Empty<(string, string, string)>(),
            ["PULSE_SEG_LENGTH_ISUS90"] = Array.Empty<(string, string, string)>(),
            ["WEAPON_CARRIER"]          = Array.Empty<(string, string, string)>(),
            ["DIST_LIST"]               = Array.Empty<(string, string, string)>(),
            ["DIST_TO"]                 = Array.Empty<(string, string, string)>(),
            ["FIELD_CONV"]              = Array.Empty<(string, string, string)>(),
            ["IF_SYSTEMS"]              = Array.Empty<(string, string, string)>(),
            ["CD_BACKUP_LOG"]           = Array.Empty<(string, string, string)>(),
            ["CD_CHANGE_LOG"]           = Array.Empty<(string, string, string)>(),
        };

        // ── 전체 Oracle 테이블 목록 (36개) ───────────────────────────────────────
        public static readonly string[] AllTables =
        {
            "ACTIVE_SONAR", "AI_SNIPPET", "CD_BACKUP_LOG", "CD_CHANGE_LOG",
            "CHARACTERISTICS", "CLASSES", "COUNTRY_CODES", "DB_DIST_GROUP",
            "DB_DIST_LOG", "DIST_LIST", "DIST_TO", "FIELD_CONV",
            "FRAME_SNIPPET", "HARMONICS", "IF_SYSTEMS", "IMAGE_DATA",
            "NB_SNIPPET", "PULSES", "PULSE_FREQUENCY_ISUS90", "PULSE_SEGMENTS",
            "PULSE_SEG_LENGTH_ISUS90", "PULSE_SEG_SWING_ISUS90", "SIG_SNIPPET",
            "SIGNATURES", "TRANSIENT", "TR_FRAME", "TR_SNIPPET",
            "VESSEL_DATA", "WEAPON", "WEAPON_CARRIER",
            // 배포 관련
            "DISTRIB_INFO_GROUP",
            // 암호 (ADO/Access — Oracle 아님)
            // "NAME" — Password 테이블은 MS Access, 마이그레이션 대상 아님
        };

        // ── 루트 진입점 (FK 부모가 없는 최상위 테이블) ───────────────────────────
        public static readonly string[] RootTables =
        {
            "COUNTRY_CODES", "CLASSES", "ACTIVE_SONAR", "WEAPON",
            "IF_SYSTEMS", "FIELD_CONV",
        };

        /// <summary>
        /// Oracle SELECT에서 CRYPTO.DECRYPT 래핑이 필요한 컬럼인지 확인
        /// </summary>
        public static bool IsCryptoColumn(string tableName, string columnName)
        {
            return CryptoFields.TryGetValue(tableName, out var cols) &&
                   Array.Exists(cols, c => c.Equals(columnName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Oracle SELECT 쿼리에서 특정 컬럼을 CRYPTO.DECRYPT() 래핑하여 반환
        /// </summary>
        public static string WrapDecrypt(string tableName, string columnName)
        {
            return IsCryptoColumn(tableName, columnName)
                ? $"CRYPTO.DECRYPT({tableName}.{columnName}) AS {columnName}"
                : columnName;
        }
    }
}
