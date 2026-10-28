using System.Data.Common;
using System.Data.Odbc;
using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;

namespace RawCollectMigrator.Features.DbToDb
{
    // ════════════════════════════════════════════════════════════════════════════
    //  [가이드:ORACLE연결] [GUIDE:ORACLE-CONNECTION] [찾기:소스접속] [찾기:DSN]
    //  ▣ 이 파일 = 소스 DB(Oracle/MsSQL) 연결·방언(dialect) 허브.
    //    · 연결 생성      : CreateConnection()  — ODBC=OdbcConnection / 다이렉트=OracleConnection·SqlConnection
    //    · 접속 방식      : DbConnectionMode (ODBC=폐쇄망 기본 / Direct=관리형)
    //    · 소스 종류      : DbProviderKind (Oracle / MsSql). 타겟은 항상 MsSQL(NAIMS2) 다이렉트.
    //    · 기본 접속문자열: DefaultOracleOdbcSource(로컬 리그 DSN=LegacyNaims) / DefaultTarget(localhost NAIMS2)
//      ★ 비밀번호 원칙: 로컬 리그 계정(LEGACYNAIMS/NAIMS12)은 이 저장소가 만든 격리 DB 라
//        기본값으로 둔다(값을 잃으면 리그를 못 쓴다). 현장·실운영 계정(NAIMS2DB/NaimsMig)은
//        비워 두고 시작 화면에서 1회 입력받아 DPAPI 로만 저장한다. [찾기:비밀번호저장]
    //    · 파라미터 표기  : ParamMarker() — ODBC '?' / Oracle ':name' / MsSQL '@name'
    //    · 방언 차이      : QuoteIdent()·QuoteColumn()(식별자 인용), RowCountSql/TopRowsSql(ROWNUM vs TOP), ColumnsSql(all_tab_columns vs information_schema)
    //  ▶ 주의: 12c 소스는 ODP.NET Core(19c+ 전용)로 '다이렉트' 불가 → 반드시 'ODBC'.
    // ════════════════════════════════════════════════════════════════════════════
    /// <summary>마이그레이션 소스 DB 종류. 타겟은 항상 MsSQL(NAIMS2).</summary>
    public enum DbProviderKind
    {
        MsSql,
        Oracle
    }

    // [NAIMS-ISSUE:CONN-MODE] 소스 접속 방식 — ODBC(폐쇄망 기본) vs 다이렉트(관리형 라이브러리)
    /// <summary>
    /// 소스 DB 접속 방식. 폐쇄망 현행은 ODBC(윈도우 ODBC 드라이버 관리자 경유)이고,
    /// 다이렉트는 벤더 관리형 드라이버(OracleConnection / SqlConnection)로 직접 붙는다.
    /// 방언(식별자 인용·TOP/ROWNUM·카탈로그 뷰)은 <see cref="DbProviderKind"/> 로만 갈리고,
    /// 접속 방식은 여기서 (a) 어떤 연결 객체를 만드느냐 (b) 파라미터 표기(? vs :/@) 두 가지만 바꾼다.
    /// </summary>
    public enum DbConnectionMode
    {
        /// <summary>Windows ODBC 드라이버 관리자 경유(DSN 또는 DSN-less). 폐쇄망 기본값.</summary>
        Odbc,
        /// <summary>벤더 관리형 드라이버로 직접 접속(OracleConnection / SqlConnection).</summary>
        Direct
    }

    /// <summary>
    /// 소스 DB가 MsSQL / Oracle 두 종류일 수 있으므로 연결 생성과
    /// 방언(dialect) 차이(식별자 인용, TOP vs FETCH, 카탈로그 뷰)를 여기서 흡수한다.
    /// 접속 방식(ODBC/다이렉트)까지 <see cref="DbConnectionMode"/> 로 함께 흡수한다.
    /// </summary>
    public static class DbProvider
    {
        // [NAIMS-ISSUE:DB-DIALECT] Oracle vs MsSQL 방언·연결 팩토리

        /// <summary>
        /// 접속 방식을 반영한 연결 생성.
        ///   · ODBC   → System.Data.Odbc.OdbcConnection (Windows ODBC 드라이버/DSN 경유)
        ///   · 다이렉트 → 벤더 관리형(OracleConnection / SqlConnection)
        /// </summary>
        // [찾기:접속계측] 접속 실패 시 "무엇으로 · 어떤 문자열로" 시도했는지 정확히 남긴다.
        //   화면에 보이는 값과 실제 전달된 값이 다를 수 있어(저장본 복원·전환 타이밍 등),
        //   메시지만으로는 구분이 안 된다. 길이까지 찍어 보이지 않는 문자·공백도 드러낸다.
        /// <summary>이번 접속 시도가 어떤 연결 객체·어떤 문자열로 이뤄지는지 설명한다(비밀번호 마스킹).</summary>
        public static string DescribeAttempt(DbProviderKind kind, DbConnectionMode mode, string connectionString)
        {
            var cls = mode == DbConnectionMode.Odbc
                ? "OdbcConnection"
                : (kind == DbProviderKind.Oracle ? "OracleConnection" : "SqlConnection");
            var masked = System.Text.RegularExpressions.Regex.Replace(
                connectionString ?? "", @"(?i)(password|pwd)\s*=\s*[^;]*", "$1=***");
            return $"[시도] {cls} · {DisplayName(kind)}/{ModeName(mode)} · 길이 {(connectionString ?? "").Length}"
                 + "\n        " + masked;
        }

        public static DbConnection CreateConnection(DbProviderKind kind, DbConnectionMode mode, string connectionString)
        {
            if (mode == DbConnectionMode.Odbc)
                return new OdbcConnection(connectionString);
            return kind switch
            {
                DbProviderKind.Oracle => new OracleConnection(connectionString),
                _ => new SqlConnection(connectionString),
            };
        }

        /// <summary>접속 방식 미지정 시 다이렉트로 붙는다(타겟 NAIMS2 는 항상 SqlClient 다이렉트).</summary>
        public static DbConnection CreateConnection(DbProviderKind kind, string connectionString) =>
            CreateConnection(kind, DbConnectionMode.Direct, connectionString);

        /// <summary>
        /// 파라미터 표기. ODBC 는 이름이 없는 위치 기반 '?' 를 쓴다(드라이버가 순서로 바인딩).
        /// 다이렉트는 명명 파라미터(MsSQL '@name', Oracle ':name').
        /// </summary>
        public static string ParamMarker(DbProviderKind kind, DbConnectionMode mode, string name) =>
            mode == DbConnectionMode.Odbc ? "?" : ParamPrefix(kind) + name;

        /// <summary>연결 확인용 최소 쿼리. Oracle 은 FROM 절이 필수라 DUAL 을 쓴다.</summary>
        public static string PingSql(DbProviderKind kind) => kind switch
        {
            DbProviderKind.Oracle => "SELECT 1 FROM DUAL",
            _ => "SELECT 1",
        };

        /// <summary>서버 버전 문자열 조회 쿼리.</summary>
        public static string VersionSql(DbProviderKind kind) => kind switch
        {
            DbProviderKind.Oracle => "SELECT banner FROM v$version WHERE ROWNUM = 1",
            _ => "SELECT @@VERSION",
        };

        // [NAIMS-ISSUE:ORACLE-OWNER-SCOPE] [찾기:owner한정] Oracle 카탈로그 조회의 owner 한정 규칙
        //   all_tables/all_tab_columns 를 owner 무필터로 읽으면 DBA 급 계정(system 등)에서
        //   같은 이름의 테이블이 여러 스키마(내장 스키마 포함)에 보여 중복/오판이 난다.
        //   규칙: ① 현재 로그온 스키마(SYS_CONTEXT('USERENV','CURRENT_SCHEMA'))가 그 테이블을 가지면 그것을 최우선
        //          (레거시 DSN=naims 는 system 접속·SYSTEM 소유 테이블이므로, SYSTEM 이 내장 스키마여도 이 분기로 잡힌다)
        //         ② 없으면 내장 스키마 제외 후 MIN(owner) 로 결정적 1개 선택
        //          (복구 스키마 LEGACYNAIMS 를 다른 계정으로 접속해 볼 때 이 분기로 잡힌다)
        //   ※ 데이터 조회(RowCountSql/TopRowsSql 의 COUNT/SELECT)는 현행 그대로 owner 무한정 —
        //     로그온 스키마/시노님 해석에 의존한다(아래 각 메서드 주석 참조).
        private const string OracleOwnerFilter =
            "(t.owner = SYS_CONTEXT('USERENV','CURRENT_SCHEMA') " +
            "OR (t.owner NOT IN ('SYS','SYSTEM','SYSMAN','XDB','MDSYS','CTXSYS','ORDSYS','ORDDATA','ORDPLUGINS'," +
            "'OUTLN','WMSYS','DBSNMP','APPQOSSYS','OLAPSYS','LBACSYS','DVSYS','AUDSYS','OJVMSYS'," +
            "'GSMADMIN_INTERNAL','ANONYMOUS','XS$NULL','SI_INFORMTN_SCHEMA','DBSFWUSER','REMOTE_SCHEDULER_AGENT') " +
            "AND t.owner NOT LIKE 'APEX%'))";

        /// <summary>테이블 존재 확인. 파라미터 1개(? / @t / :t) 로 테이블명을 받는다.
        /// Oracle 은 [찾기:owner한정] 규칙으로 내장 스키마 소유분을 제외하고 판정한다.</summary>
        public static string TableExistsSql(DbProviderKind kind, DbConnectionMode mode)
        {
            string t = ParamMarker(kind, mode, "t");
            return kind switch
            {
                // Oracle 은 식별자가 기본 대문자로 저장된다.
                DbProviderKind.Oracle => $@"
SELECT 1 FROM all_tables t
 WHERE t.table_name = UPPER({t})
   AND {OracleOwnerFilter}
   AND ROWNUM = 1",
                _ => $"SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = {t}",
            };
        }

        /// <summary>컬럼 스키마 조회. 반환 순서: 컬럼명, 타입, 최대길이, Null허용, 순번.
        /// Oracle 은 [찾기:owner한정] 규칙으로 owner 1개를 결정적으로 골라(로그온 스키마 우선 →
        /// 내장 제외 후 MIN(owner)) 다중 스키마 중복 행을 막는다. 파라미터는 여전히 1개(테이블명).</summary>
        public static string ColumnsSql(DbProviderKind kind, DbConnectionMode mode)
        {
            string t = ParamMarker(kind, mode, "t");
            return kind switch
            {
                DbProviderKind.Oracle => $@"
SELECT column_name, data_type, data_length, nullable, column_id
  FROM all_tab_columns c
 WHERE c.table_name = UPPER({t})
   AND c.owner = (SELECT MIN(t.owner) KEEP (DENSE_RANK FIRST ORDER BY
                         CASE WHEN t.owner = SYS_CONTEXT('USERENV','CURRENT_SCHEMA') THEN 0 ELSE 1 END)
                    FROM all_tables t
                   WHERE t.table_name = c.table_name
                     AND {OracleOwnerFilter})
 ORDER BY column_id",
                _ => $@"
SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE, ORDINAL_POSITION
  FROM INFORMATION_SCHEMA.COLUMNS
 WHERE TABLE_NAME = {t}
 ORDER BY ORDINAL_POSITION",
            };
        }

        /// <summary>파라미터 접두어. MsSQL 은 '@', Oracle 은 ':'.</summary>
        public static string ParamPrefix(DbProviderKind kind) => kind == DbProviderKind.Oracle ? ":" : "@";

        /// <summary>테이블명을 방언에 맞게 안전하게 인용한다.</summary>
        public static string QuoteIdent(DbProviderKind kind, string name)
        {
            if (kind == DbProviderKind.Oracle)
            {
                // Oracle: 대문자 정규화 후 큰따옴표. 스키마.테이블 형태 지원.
                var parts = name.Split('.');
                return string.Join(".", parts.Select(p => "\"" + p.Replace("\"", "").ToUpperInvariant() + "\""));
            }
            var esc = name.Replace("]", "]]");
            if (esc.Contains('.'))
                return string.Join(".", esc.Split('.').Select(p => "[" + p + "]"));
            return "[" + esc + "]";
        }

        /// <summary>행 수 조회 쿼리. Oracle 엔 COUNT_BIG 이 없다.
        /// ※ [찾기:owner한정] 데이터 조회는 의도적으로 owner 무한정 — 스키마 접두 없이
        /// 현재 로그온 스키마/시노님 해석에 맡긴다(카탈로그 조회와 달리 현행 유지).</summary>
        public static string RowCountSql(DbProviderKind kind, string quotedTable) => kind switch
        {
            DbProviderKind.Oracle => $"SELECT COUNT(*) FROM {quotedTable}",
            _ => $"SELECT COUNT_BIG(*) FROM {quotedTable}",
        };

        /// <summary>상위 N행 조회 쿼리. MsSQL 은 TOP + NOLOCK, Oracle 은 ROWNUM.
        /// ※ [찾기:owner한정] 데이터 조회는 의도적으로 owner 무한정(로그온 스키마/시노님 의존 — 현행 유지).</summary>
        public static string TopRowsSql(DbProviderKind kind, string quotedTable, string columnList, int top) => kind switch
        {
            DbProviderKind.Oracle => $"SELECT {columnList} FROM {quotedTable} WHERE ROWNUM <= {top}",
            _ => $"SELECT TOP {top} {columnList} FROM {quotedTable} WITH (NOLOCK)",
        };

        /// <summary>그리드 전시에서 제외할 대용량/특수 타입인지.</summary>
        public static bool IsBinaryLike(DbProviderKind kind, string dataType)
        {
            var t = dataType.ToLowerInvariant();
            if (kind == DbProviderKind.Oracle)
                return t is "blob" or "clob" or "nclob" or "bfile" or "long" or "long raw" or "raw" or "xmltype";
            return t is "varbinary" or "binary" or "image" or "text" or "ntext" or "xml" or "sql_variant";
        }

        /// <summary>컬럼명 인용 (SELECT 목록용).</summary>
        public static string QuoteColumn(DbProviderKind kind, string col) => kind == DbProviderKind.Oracle
            ? "\"" + col.Replace("\"", "") + "\""
            : "[" + col.Replace("]", "]]") + "]";

        /// <summary>UI 표시용 이름.</summary>
        public static string DisplayName(DbProviderKind kind) => kind == DbProviderKind.Oracle ? "Oracle" : "MsSQL";

        // ── 알려진 환경의 기본 접속 문자열 ──────────────────────────────

        // [NAIMS-ISSUE:CONNSTR-DEFAULTS] 소스·타깃 표준 접속문자열(서버·계정·암호 변경 지점)
        // [NAIMS-ISSUE:NO-EMBEDDED-SECRET] 현장·실운영 계정의 비밀번호는 기본값에 넣지 않는다.
        //   Pwd= 를 비워 두면 시작 화면이 입력을 요구하고, 저장은 DPAPI 로만 한다.
        //   (로컬 리그 계정은 예외 — 아래 Oracle 기본값 참조)
        /// <summary>
        /// 샘플 소스: MsSQL NAIMS (192.168.111.39:1433) — 기준식별 DB, 42 테이블.
        /// 2026-07-21 NaimsMig 읽기 전용 계정으로 접속 검증 완료.
        /// ★ 현장 계정이므로 비밀번호는 비워 둔다 — 시작 화면에서 입력.
        /// </summary>
        public const string DefaultMsSqlSource =
            "Server=tcp:192.168.111.39,1433;Database=NAIMS;User Id=NaimsMig;Password=;Encrypt=false;TrustServerCertificate=true;Connect Timeout=15;";

        // [NAIMS-ISSUE:ORACLE-DIRECT-12C] 정정(2026-08-19 실측): 12c 다이렉트 접속은 된다.
        //   예전 주석은 "ODP.NET Core 는 19c+ 만 지원 → 12c 는 ODBC 만 가능" 이라고 단정했으나,
        //   Oracle.ManagedDataAccess 로 12.2.0.1 에 접속해 LEGACYNAIMS 42 테이블 조회까지 확인했다.
        //   (서비스명 LegacyNaims / NAIMS12 양쪽 모두 성공)
        //   ★ 다이렉트를 기본으로 쓴다 — 관리형 드라이버 DLL 하나면 되고 Oracle 클라이언트·DSN 등록·
        //     비트수 일치가 전혀 필요 없다. ODBC 는 그 셋이 모두 맞아야 하고, 하나라도 어긋나면
        //     IM002 로 막힌다(현장에서 진단하기 가장 어려운 실패 유형).
        /// <summary>Oracle 소스 다이렉트(Easy Connect) 기본값 — 관리형 드라이버, 클라이언트 설치 불필요.</summary>
        public const string DefaultOracleSource =
            "User Id=LEGACYNAIMS;Password=naims2!;Data Source=localhost:1522/LegacyNaims;";

        /// <summary>이관 목표(기본=로컬 테스트). ⚠️ 실운영 205 는 사고방지 위해 기본값에서 제외 — 필요 시 명시 지정.
        /// 실운영: "Server=tcp:192.168.200.205,1433;Database=NAIMS2;User Id=NAIMS2DB;Password=&lt;입력&gt;;Encrypt=true;TrustServerCertificate=true;"</summary>
        public const string DefaultTarget =
            "Server=localhost;Database=NAIMS2;Trusted_Connection=yes;Encrypt=false;TrustServerCertificate=true;";

        // [NAIMS-ISSUE:ODBC-CONNSTR] ODBC 접속문자열 기본 템플릿 (DSN-less 예시)
        //   · DSN-less  : Driver={...} 로 드라이버를 직접 지정 — 각 PC 에 드라이버만 설치돼 있으면 됨.
        //   · DSN 방식  : "DSN=<등록이름>;Uid=...;Pwd=...;" 로 바꿔 쓰면 됨(ODBC 데이터 원본 관리자에 사전 등록).
        //   드라이버 이름(Oracle in ..., ODBC Driver 17 for SQL Server)은 설치 환경에 맞게 조정한다.

        /// <summary>MsSQL 소스 ODBC(DSN-less) 기본 템플릿.</summary>
        public const string DefaultMsSqlOdbcSource =
            "Driver={ODBC Driver 17 for SQL Server};Server=tcp:192.168.111.39,1433;Database=NAIMS;Uid=NaimsMig;Pwd=;TrustServerCertificate=yes;";

        // [NAIMS-ISSUE:LEGACY-ODBC-DSN] 레거시 실측(USPASK_CDMU DBConnectionManager.cpp)
        //   원 시스템은 순수 ODBC + System DSN 으로 붙는다:
        //     SQLConnect(DSN="naims", user="system", pwd=<레거시 소스 참조>)   ← 현재 운영 경로
        //     (대안 경로로 DSN="USPASK" / 계정 Scott 도 코드에 주석으로 남아 있다)
        //   ※ 실제 비밀번호는 여기 적지 않는다 — 레거시 소스(USPASK_CDMU DBConnectionManager.cpp) 참조.
        //   그래서 Oracle ODBC 기본값을 DSN 형식으로 맞춘다. 같은 PC 라면 이 DSN 을 그대로 재사용.
        // [NAIMS-ISSUE:USER-DSN-INVISIBLE] 기본은 시스템 DSN(naims)을 쓴다.
        //   사용자 DSN(HKCU, 예: LegacyNaims)은 프로세스 권한·계정에 따라 안 보일 수 있고
        //   (관리자 실행 시 특히), 그러면 ODBC 가 IM002 "데이터 원본 이름 없음"을 던진다.
        //   시스템 DSN(HKLM)은 모든 프로세스에서 보인다. 둘 다 같은 12c/1522 를 가리킨다.
        //   ※ ODBC 는 이제 '폴백' 이다 — 기본은 다이렉트. 현장 레거시 앱이 ODBC 를 쓴다는 사실과
        //     우리 이관 도구가 무엇으로 붙느냐는 무관하다(우리는 읽기만 한다).
        /// <summary>Oracle 소스 ODBC 기본 템플릿 — 시스템 DSN 'naims'(12c/1522), 유저 LEGACYNAIMS.
        /// DSN-less 대안: "Driver={Oracle in OraDB12Home1};DBQ=localhost:1522/LegacyNaims;Uid=LEGACYNAIMS;Pwd=&lt;입력&gt;;"
        /// 오프라인 w/s: 12c DB=LegacyNaims + 유저 LEGACYNAIMS (비밀번호는 시작 화면에서 입력). </summary>
        public const string DefaultOracleOdbcSource =
            "DSN=naims;Uid=LEGACYNAIMS;Pwd=naims2!;";

        /// <summary>다이렉트 기준 소스 기본 접속문자열(하위호환).</summary>
        public static string DefaultSourceFor(DbProviderKind kind) =>
            DefaultSourceFor(kind, DbConnectionMode.Direct);

        /// <summary>소스 종류 × 접속 방식에 맞는 기본 접속문자열 템플릿.</summary>
        public static string DefaultSourceFor(DbProviderKind kind, DbConnectionMode mode)
        {
            if (mode == DbConnectionMode.Odbc)
                return kind == DbProviderKind.Oracle ? DefaultOracleOdbcSource : DefaultMsSqlOdbcSource;
            return kind == DbProviderKind.Oracle ? DefaultOracleSource : DefaultMsSqlSource;
        }

        /// <summary>UI 표시용 접속 방식 이름.</summary>
        public static string ModeName(DbConnectionMode mode) => mode == DbConnectionMode.Odbc ? "ODBC" : "다이렉트";
    }
}
