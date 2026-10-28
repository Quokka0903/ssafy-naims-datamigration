using System.Data.Common;
using System.IO;
using System.Text;
using Microsoft.Data.SqlClient;

using RawCollectMigrator.Infrastructure;

namespace RawCollectMigrator.Features.DbToDb
{
    // ════════════════════════════════════════════════════════════════════════════
    //  [가이드:복호화] [가이드:암호화] [GUIDE:DECRYPT] [GUIDE:ENCRYPT] [찾기:이름복호] [찾기:CRYPTO]
    //  ▣ 이 파일 = 이름컬럼(암호화) 복호화 두뇌. "이름이 왜 못 읽히나/깨지나"는 여기부터.
    //    · 소스=MsSQL : 대칭키 OPEN SYMMETRIC KEY + DECRYPTBYKEY  (상수 SymmetricKeyName / CertificateName)
    //    · 소스=Oracle: SELECT 식에 복호 인라인. 자동추출 DiscoverOracleCryptoAsync() 가
    //                   all_objects 후보 + 여러 템플릿을 표본에 적용→판독점수(ReadabilityScore)로 최고식 채택.
    //                   채택 예: CRYPTO.DECRYPT(UTL_RAW.CAST_TO_VARCHAR2({col}))   (12c 레거시 DES 키 'hellios#')
    //    · ★ 함정: 이름컬럼이 RAW(200) 이면 반드시 UTL_RAW.CAST_TO_VARCHAR2 로 감싼 뒤 CRYPTO.DECRYPT.
    //             RAW 를 직접 넘기면 암묵 RAW→HEX 문자열 변환으로 복호가 깨지거나 ORA-28232(8배수 위반).
    //    · 대상 컬럼 : OracleEncryptedKeys / EncryptedColumns (CLASSES/VESSEL_DATA/ACTIVE_SONAR/TRANSIENT 이름 3종)
    //    · 적용 지점 : SelectExpression()·DecryptExpressionOnly() ← 이관 SELECT 시 컬럼식 생성(여기서 복호가 걸린다)
    //    · 키 개폐   : OpenKeyAsync()/CloseKeyAsync() (MsSQL 전용, Oracle 은 인라인이라 개폐 없음)
    //  ▶ UI [Oracle 암호화 추출] 버튼 = DiscoverOracleCryptoAsync 호출. 헤드리스는 이 메서드를 직접 호출해야 복호가 걸린다.
    // ════════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// 암호화 컬럼을 어떤 방식으로 평문화할지.
    /// </summary>
    public enum DecryptMode
    {
        /// <summary>아직 판정 전.</summary>
        Unknown,

        /// <summary>
        /// 소스 DB에 대칭키가 살아 있어 DECRYPTBYKEY 로 직접 복호화.
        /// ★ 정상 경로. 키 확보 시 자동으로 이 모드가 선택된다.
        /// </summary>
        SymmetricKey,

        /// <summary>
        /// 키가 없어 외부에서 받은 "소스PK,평문이름" CSV 로 대체.
        /// 레거시 클라이언트 화면에서 뽑아낸 목록을 주입하는 우회 경로.
        /// </summary>
        ExternalMap,

        /// <summary>
        /// 키도 CSV도 없을 때 'UNKNOWN_&lt;소스ID&gt;' 로 채워 구조만 이관.
        /// 사람이 읽을 수 없으므로 최후 수단.
        /// </summary>
        Placeholder,

        /// <summary>복호화 불가 — 해당 테이블 이관을 중단한다.</summary>
        Blocked
    }

    /// <summary>암호화된 컬럼 한 건.</summary>
    public sealed record EncryptedColumn(string Table, string Column, int TargetMaxLength, bool Required)
    {
        public string Key => $"{Table}.{Column}";
    }

    /// <summary>
    /// 구 NAIMS 의 ENCRYPTBYKEY 컬럼 복호화 추상화 계층.
    ///
    /// 레거시 구조 (Old_DB_Script.sql / Library_DBInterface 실측):
    ///   Database Master Key
    ///     └ CERTIFICATE     certificate_manager
    ///         └ SYMMETRIC KEY symmetric_key_AASManager   (key_guid = ExpectedKeyGuid)
    ///
    ///   EXEC Open_Symmetric_Key                       -- OPEN SYMMETRIC KEY ... DECRYPTION BY CERTIFICATE ...
    ///   SELECT RTRIM([dbo].[FN_Decryption](CLASS_NAME)) FROM CLASSES
    ///
    /// ★ 정정(2026-08-07 실측): 사본 DB(192.168.111.39/NAIMS)에도 대칭키(symmetric_key_AASManager,
    /// key_guid E2B93F00-8C4D-4A32-9822-2A27060FA4C8, AES_256)·인증서(certificate_manager)·
    /// FN_Decryption 이 "모두 존재"한다. 앞선 "없다"는 판단은 오판이었다 — db_datareader 계정(NaimsMig)이
    /// 메타데이터 가시성 때문에 sys.symmetric_keys 를 0건으로 봤을 뿐이고, OPEN SYMMETRIC KEY + DECRYPTBYKEY
    /// 는 그 계정으로도 정상 복호된다(실명 복호 검증 완료). 권한 부족 시 GrantScript() 참조.
    /// </summary>
    public sealed class CryptoContext
    {
        public const string SymmetricKeyName = "symmetric_key_AASManager";
        public const string CertificateName  = "certificate_manager";

        /// <summary>암호문 앞 16바이트에서 관측된 키 GUID. 이 값이면 우리 데이터를 풀 수 있는 키다.</summary>
        public const string ExpectedKeyGuid = "e2b93f00-8c4d-4a32-9822-2a27060fa4c8";

        /// <summary>
        /// ENCRYPTBYKEY 로 저장된 varbinary 컬럼 목록 (2026-07-22 실측).
        /// Required = true 인 것은 타겟이 평문 varchar 라 복호화 없이는 이관 불가.
        /// OPERATOR / PASSWORD / ONA_* 는 이관 대상이 아니므로 Required = false.
        /// </summary>
        public static readonly IReadOnlyList<EncryptedColumn> EncryptedColumns = new List<EncryptedColumn>
        {
            new("CLASSES",      "CLASS_NAME",          100, true),
            new("CLASSES",      "CLASS_NAME_K",        100, true),
            new("CLASSES",      "CLASS_NAME_ISUS90",   100, true),
            new("VESSEL_DATA",  "VESSEL_NAME",         100, true),
            new("VESSEL_DATA",  "VESSEL_NAME_K",       100, true),
            new("VESSEL_DATA",  "VESSEL_NAME_ISUS90",  100, true),
            new("ACTIVE_SONAR", "SONAR_NAME",          100, true),
            new("ACTIVE_SONAR", "SONAR_NAME_ISUS90",   100, true),
            new("TRANSIENT",    "TR_NAME",              50, true),
            // ── 이관 대상 아님 (참고용으로만 등록) ──
            new("ONA_209",      "VESSEL_NAME",         100, false),
            new("ONA_214",      "VESSEL_NAME",         100, false),
            new("OPERATOR",     "OPERATOR_NAME",       100, false),
            new("OPERATOR",     "OPERATOR_ID_STR",     100, false),
            new("OPERATOR",     "OPERATOR_PASSWORD",   100, false),
            new("PASSWORD",     "USER_ID",             100, false),
            new("PASSWORD",     "USER_PASSWORD",       100, false),
            new("PASSWORD",     "USER_NAME",           100, false),
        };

        private static readonly HashSet<string> EncryptedKeys =
            new(EncryptedColumns.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);

        // [NAIMS-ISSUE:CRYPTO-COLUMN-SCOPE] 소스별 암호화 컬럼 목록
        /// <summary>
        /// Oracle 소스에서 암호화된 이름 컬럼.
        /// 2026-08-06 레거시 소스 실측(USPASK_CDMU: TableManager_Classes/VesselData/ActiveSonar/TREvent.cpp)으로
        /// 확정 — 현장 Oracle 은 이름 컬럼을 스키마 패키지 CRYPTO 로 암복호화한다:
        ///   쓰기: INSERT ... CRYPTO.ENCRYPT(?)     읽기: SELECT CRYPTO.DECRYPT(col)
        ///   (CRYPTO.ENCRYPT 는 DBMS_OBFUSCATION_TOOLKIT DES · 키 'hellios' 를 감싼 것)
        /// 암호화 범위는 MsSQL 과 동일하게 CLASSES / VESSEL_DATA / ACTIVE_SONAR / TRANSIENT 전량이다.
        ///   (기존 "ACTIVE_SONAR / TRANSIENT 는 Oracle 에선 평문" 주석은 DDL 덤프 오판독이라 정정)
        /// 로컬 XE 오프라인 덤프는 RAW 평문일 수 있으나, 복호화 식은 DiscoverOracleCryptoAsync 가
        /// 결과 판독성으로 CRYPTO.DECRYPT / RAW_TO_CHAR 중 맞는 쪽을 자동 선택하므로 두 환경 모두 대응된다.
        /// </summary>
        private static readonly HashSet<string> OracleEncryptedKeys =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "CLASSES.CLASS_NAME", "CLASSES.CLASS_NAME_K", "CLASSES.CLASS_NAME_ISUS90",
                "VESSEL_DATA.VESSEL_NAME", "VESSEL_DATA.VESSEL_NAME_K", "VESSEL_DATA.VESSEL_NAME_ISUS90",
                "ACTIVE_SONAR.SONAR_NAME", "ACTIVE_SONAR.SONAR_NAME_ISUS90",
                "TRANSIENT.TR_NAME",
                // DATASET.DET_VESSEL 도 CRYPTO 암호화이나 0건·이관 제외 대상이라 등록하지 않음.
            };

        public DecryptMode Mode { get; private set; } = DecryptMode.Unknown;

        // [NAIMS-ISSUE:ORACLE-DECRYPT-TEMPLATE] 세션 정적 복호화식(재기동 시 재추출 필요)
        /// <summary>
        /// 현장 Oracle 에서 발견한 복호화 식 템플릿. "{col}" 자리에 암호화 컬럼이 치환된다.
        /// 예: "DES_DECRYPT({col})" 또는 "UTL_RAW.CAST_TO_VARCHAR2(SCHEMA.PKG.DECRYPT({col}))".
        /// 세션 정적 — [Oracle 암호화 추출] 버튼이 설정하면 이후 모든 실행이 사용한다. null = 미발견(차단).
        /// </summary>
        public static string? OracleDecryptTemplate { get; set; }

        /// <summary>진단 메시지 (UI 전시용).</summary>
        public string Diagnosis { get; private set; } = "미확인";

        /// <summary>키가 실제로 존재하고 복호화가 검증되었는지.</summary>
        public bool CanDecrypt => Mode == DecryptMode.SymmetricKey;

        // [NAIMS-ISSUE:USE-DECRYPTION] 복호화 불러오기 ON/OFF [찾기:복호토글]
        /// <summary>
        /// 복호화 사용 여부. false 면 ProbeAsync 는 탐색을 건너뛰고, SelectExpression/DecryptExpressionOnly 는
        /// 암호화 컬럼이라도 복호식으로 감싸지 않고 <b>원문 컬럼 그대로</b> 돌려준다.
        /// (이미 평문으로 dump 된 소스처럼 CRYPTO 를 찾을 수 없거나 적용하면 안 되는 경우)
        /// </summary>
        public bool DecryptionEnabled { get; set; } = true;

        /// <summary>ExternalMap 모드에서 사용하는 "테이블.컬럼" → (소스PK → 평문) 사전.</summary>
        private readonly Dictionary<string, Dictionary<long, string>> _externalMap =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>현재 소스 종류. ProbeAsync 가 설정한다.</summary>
        public DbProviderKind SourceKind { get; private set; } = DbProviderKind.MsSql;

        public static bool IsEncrypted(DbProviderKind kind, string table, string column) =>
            kind == DbProviderKind.Oracle
                ? OracleEncryptedKeys.Contains($"{table}.{column}")
                : EncryptedKeys.Contains($"{table}.{column}");

        public bool IsEncrypted(string table, string column) => IsEncrypted(SourceKind, table, column);

        /// <summary>해당 테이블에 복호화가 반드시 필요한 컬럼이 있는지.</summary>
        public static bool RequiresDecryption(DbProviderKind kind, string table) =>
            kind == DbProviderKind.Oracle
                ? OracleEncryptedKeys.Any(k => k.StartsWith(table + ".", StringComparison.OrdinalIgnoreCase))
                : EncryptedColumns.Any(c => c.Required &&
                      string.Equals(c.Table, table, StringComparison.OrdinalIgnoreCase));

        /// <summary>MsSQL 기준(기본). 소스 종류별 판정은 오버로드를 쓴다.</summary>
        public static bool RequiresDecryption(string table) =>
            RequiresDecryption(DbProviderKind.MsSql, table);

        // ─────────────────────────────────────────────────────────────
        //  1) 능력 판정
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 소스 DB에 대칭키가 살아 있는지 확인하고, 실제로 한 건 복호화해 검증한다.
        /// 인증서를 복원하면 이 메서드가 자동으로 SymmetricKey 를 반환한다.
        /// </summary>
        public async Task<DecryptMode> ProbeAsync(
            DbProviderKind kind, DbConnectionMode mode, string sourceConnStr, CancellationToken ct = default)
        {
            SourceKind = kind;

            // [찾기:복호토글] 복호화 비활성이면 어떤 소스든 탐색 없이 통과 — 이름 컬럼을 원문 그대로 이관.
            if (!DecryptionEnabled)
            {
                Mode = DecryptMode.Blocked;   // CanDecrypt=false 지만, NeedsDecryption 이 false 라 차단되지 않는다.
                Diagnosis = "복호화 비활성 — 이름 컬럼을 원문(평문 가정) 그대로 이관.";
                return Mode;
            }

            if (kind == DbProviderKind.Oracle)
            {
                // Oracle 은 CLASSES / VESSEL_DATA 의 이름이 RAW 로 앱단 암호화돼 있다
                // (알고리즘 DES, 키 'hellios#' = RPAD('hellios',8,'#') — CryptIT/오라클암호화.txt 실측·검증
                //  벡터 'A201'→F45F4284A4E23E62. 과거 'AcousticAnalysisSystem' 주석은 오판이라 정정).
                // 복호화 함수/식은 현장 Oracle 에서 [Oracle 암호화 추출] 버튼으로 발견해 OracleDecryptTemplate 에 담는다.
                if (!string.IsNullOrWhiteSpace(OracleDecryptTemplate))
                {
                    // 발견된 식이 실제로 평문을 돌려주는지 표본으로 검증한다.
                    Mode = await ValidateOracleTemplateAsync(sourceConnStr, mode, ct)
                        ? DecryptMode.SymmetricKey : DecryptMode.Blocked;
                    Diagnosis = Mode == DecryptMode.SymmetricKey
                        ? $"Oracle 복호화 식 적용됨: {OracleDecryptTemplate}"
                        : $"Oracle 복호화 식 검증 실패: {OracleDecryptTemplate} — [Oracle 암호화 추출] 재실행 필요";
                    return Mode;
                }

                Mode = DecryptMode.Blocked;
                Diagnosis =
                    "Oracle 소스: CLASSES / VESSEL_DATA 의 이름이 RAW(200) 으로 암호화됨(추정 DES). " +
                    "[Oracle 암호화 추출] 버튼으로 현장에서 복호화 함수를 찾아 적용하세요. " +
                    "(ACTIVE_SONAR / TRANSIENT 는 Oracle 에선 평문)";
                return Mode;
            }

            try
            {
                // MsSQL 소스도 ODBC/다이렉트 접속 방식을 그대로 따른다. 복호화 검증에 쓰는
                // T-SQL(sys.* 조회·OPEN SYMMETRIC KEY·DECRYPTBYKEY)은 ODBC 로도 동작하되,
                // 명명 파라미터(@n)는 ODBC 에서 위치 기반 '?' 로 바뀌어야 한다.
                string nMark = DbProvider.ParamMarker(DbProviderKind.MsSql, mode, "n");
                await using var cn = DbProvider.CreateConnection(DbProviderKind.MsSql, mode, sourceConnStr);
                await cn.OpenAsync(ct);

                // ① 대칭키 존재 확인
                var keyGuid = await ScalarAsync(cn,
                    $"SELECT CONVERT(varchar(64), key_guid) FROM sys.symmetric_keys WHERE name = {nMark}",
                    (nMark, SymmetricKeyName), ct) as string;

                if (string.IsNullOrEmpty(keyGuid))
                {
                    // sys.symmetric_keys 는 메타데이터 가시성 규칙을 따른다.
                    // 키가 실제로 없을 수도 있지만, 접속 계정에 VIEW DEFINITION 이 없어
                    // "0건"으로 보이는 경우가 훨씬 흔하다(2026-07-22 NaimsMig 실측).
                    Mode = DecryptMode.Blocked;
                    Diagnosis =
                        $"대칭키 '{SymmetricKeyName}' 가 보이지 않는다. 접속 계정에 권한이 없거나 키가 없다. " +
                        "먼저 sysadmin 으로 GrantScript() 를 실행해 권한을 부여할 것.";
                    return Mode;
                }

                if (!string.Equals(keyGuid, ExpectedKeyGuid, StringComparison.OrdinalIgnoreCase))
                {
                    Mode = DecryptMode.Blocked;
                    Diagnosis =
                        $"대칭키는 있으나 key_guid 불일치. 기대 {ExpectedKeyGuid}, 실제 {keyGuid}. " +
                        "암호문이 다른 키로 만들어졌으므로 복호화되지 않는다.";
                    return Mode;
                }

                // ② 인증서 존재 확인
                var certOk = await ScalarAsync(cn,
                    $"SELECT 1 FROM sys.certificates WHERE name = {nMark}", (nMark, CertificateName), ct) != null;
                if (!certOk)
                {
                    Mode = DecryptMode.Blocked;
                    Diagnosis = $"인증서 '{CertificateName}' 없음 — 대칭키를 열 수 없다.";
                    return Mode;
                }

                // ③ 실제 복호화 검증 — 여기까지 통과해야 진짜 사용 가능.
                //    이 시점의 Mode 는 아직 Unknown 이므로 Mode 를 보는 OpenKeyAsync 를 쓰면
                //    키가 열리지 않아 검증이 영원히 실패한다. 무조건 여는 쪽을 직접 호출한다.
                await OpenKeyCoreAsync(cn, ct);
                var sample = await ScalarAsync(cn,
                    "SELECT TOP 1 CONVERT(varchar(300), DECRYPTBYKEY(CLASS_NAME)) FROM CLASSES WHERE CLASS_NAME IS NOT NULL",
                    null, ct) as string;
                await CloseKeyCoreAsync(cn, ct);

                if (string.IsNullOrWhiteSpace(sample))
                {
                    Mode = DecryptMode.Blocked;
                    Diagnosis = "키를 열었으나 DECRYPTBYKEY 가 NULL 반환 — 키가 암호문과 짝이 맞지 않는다.";
                    return Mode;
                }

                Mode = DecryptMode.SymmetricKey;
                Diagnosis = $"복호화 검증 성공. 표본: \"{Trunc(sample, 40)}\"";
                return Mode;
            }
            catch (OperationCanceledException) { throw; }   // 취소를 "복호화 불가"로 오진하지 않는다
            catch (Exception ex)
            {
                Mode = DecryptMode.Blocked;
                Diagnosis = "키 확인 중 오류: " + ex.Message;
                return Mode;
            }
        }

        /// <summary>키 없이 진행할 대체 수단을 지정한다(사용자 선택).</summary>
        public void UseFallback(DecryptMode fallback, string reason = "")
        {
            if (fallback is not (DecryptMode.ExternalMap or DecryptMode.Placeholder))
                throw new ArgumentException("대체 모드는 ExternalMap 또는 Placeholder 만 가능하다.", nameof(fallback));
            Mode = fallback;
            Diagnosis = fallback == DecryptMode.ExternalMap
                ? "외부 이름표 사용 " + reason
                : "임시값(UNKNOWN_*) 사용 — 이름은 나중에 보정해야 함. " + reason;
        }

        // ─────────────────────────────────────────────────────────────
        //  1-Oracle) 현장 Oracle 암호화 추출·적용
        // ─────────────────────────────────────────────────────────────

        /// <summary>텍스트가 대부분 출력 가능한 문자인지 (복호화 성공 판정용).</summary>
        private static bool IsReadableText(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            int printable = 0;
            foreach (var ch in s)
                if (ch == '\t' || !char.IsControl(ch)) printable++;
            return printable >= s.Length * 0.8;
        }

        /// <summary>
        /// 판독 가능성 점수(0~1). ASCII·한글 등 실제 글자는 가점, 제어문자와
        /// 디코드 실패표식(U+FFFD)은 감점한다. 여러 후보 복호화 식 중 "가장 잘 읽히는" 것을
        /// 고르는 데 쓴다 — 문자셋/복호화 방식이 무엇이든 결과 품질로 판정하므로 동적 대응이 된다.
        /// </summary>
        private static double ReadabilityScore(string? s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            double good = 0, bad = 0;
            foreach (var ch in s)
            {
                if (ch == '�') { bad += 3; continue; }                // 문자셋 오해석 표식
                if (ch == ' ' || ch == '\t') { good += 1; continue; } // 패딩 공백
                if (char.IsControl(ch)) { bad += 2; continue; }       // 암호문/깨진 바이트
                if (ch >= '가' && ch <= '힣') { good += 2; continue; } // 완성형 한글 — 강한 가점
                if (ch >= '一' && ch <= '鿿') { good += 2; continue; } // 한자
                if (ch >= '\u0080' && ch <= '\u00FF') { bad += 1; continue; }  // Latin-1 상위(U+0080~U+00FF, 비가시 리터럴 금지) = 모지바케 흔적
                good += 1;                                            // ASCII 글자·숫자·기호
            }
            double total = good + bad;
            return total == 0 ? 0 : good / total;
        }

        /// <summary>OracleDecryptTemplate 이 표본 한 건에서 실제 평문을 돌려주는지 검증.</summary>
        private static async Task<bool> ValidateOracleTemplateAsync(string connStr, DbConnectionMode mode, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(OracleDecryptTemplate)) return false;
            try
            {
                await using var cn = DbProvider.CreateConnection(DbProviderKind.Oracle, mode, connStr);
                await cn.OpenAsync(ct);
                var expr = OracleDecryptTemplate!.Replace("{col}", "CLASS_NAME");
                await using var cmd = cn.CreateCommand();
                cmd.CommandText = $"SELECT {expr} FROM CLASSES WHERE CLASS_NAME IS NOT NULL AND ROWNUM <= 1";
                var v = await cmd.ExecuteScalarAsync(ct);
                return v != null && v != DBNull.Value && IsReadableText(Convert.ToString(v));
            }
            catch (OperationCanceledException) { throw; }   // 취소를 "검증 실패"로 오진하지 않는다
            catch { return false; }
        }

        // [NAIMS-ISSUE:ORACLE-CRYPTO-DISCOVER] Oracle RAW 이름 복호화식 자동 추출
        /// <summary>
        /// 현장 Oracle 에서 CLASSES.CLASS_NAME(RAW) 을 평문으로 되돌리는 복호화 함수/식을 찾아
        /// OracleDecryptTemplate 에 적용한다. all_source 의 후보 함수를 나열·덤프(추출)하고,
        /// 각 후보 식을 표본 한 건에 적용해 평문이 나오면 채택한다. 성공 시 true.
        /// 읽기 전용(SELECT + 함수 호출)만 수행한다.
        /// </summary>
        public static async Task<bool> DiscoverOracleCryptoAsync(
            string oracleConnStr, DbConnectionMode mode, Action<string> log, CancellationToken ct = default)
        {
            log("═══ Oracle 암호화 추출 시작 ═══");
            await using var cn = DbProvider.CreateConnection(DbProviderKind.Oracle, mode, oracleConnStr);
            try { await cn.OpenAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log("  접속 실패: " + ex.Message); return false; }

            // 1) 후보 복호화 오브젝트 나열
            var candidates = new List<(string Owner, string Name)>();
            try
            {
                await using var cmd = cn.CreateCommand();
                cmd.CommandText =
                    "SELECT owner, object_name FROM all_objects " +
                    "WHERE object_type IN ('FUNCTION','PACKAGE') " +
                    "AND (UPPER(object_name) LIKE '%DECRYPT%' OR UPPER(object_name) LIKE '%DES%' " +
                    "OR UPPER(object_name) LIKE '%CRYPT%') ORDER BY owner, object_name";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    candidates.Add((Convert.ToString(r.GetValue(0)) ?? "", Convert.ToString(r.GetValue(1)) ?? ""));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log("  후보 조회 오류: " + ex.Message); }

            if (candidates.Count == 0)
                log("  복호화 관련 함수/패키지를 못 찾음 (권한 부족이거나 이름이 다를 수 있음).");
            else
            {
                log($"  후보 {candidates.Count}개: " + string.Join(", ", candidates.Select(c => $"{c.Owner}.{c.Name}")));
                // 소스 앞부분 덤프 — 알고리즘/키 육안 확인용(추출)
                foreach (var (owner, name) in candidates.Take(4))
                {
                    try
                    {
                        await using var sc = cn.CreateCommand();
                        // ODBC 는 위치 기반 '?', 다이렉트 Oracle 은 명명 ':o'/':n'.
                        // (Oracle 관리형 드라이버는 기본 위치 바인딩이라 이름은 라벨용일 뿐)
                        string oMark = DbProvider.ParamMarker(DbProviderKind.Oracle, mode, "o");
                        string nMark = DbProvider.ParamMarker(DbProviderKind.Oracle, mode, "n");
                        sc.CommandText = $"SELECT text FROM all_source WHERE owner={oMark} AND name={nMark} ORDER BY line";
                        AddParam(sc, "o", owner); AddParam(sc, "n", name);
                        await using var sr = await sc.ExecuteReaderAsync(ct);
                        int ln = 0;
                        log($"  ── {owner}.{name} 소스 ──");
                        while (await sr.ReadAsync(ct) && ln++ < 30)
                            log("    " + (Convert.ToString(sr.GetValue(0)) ?? "").TrimEnd());
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* 소스 열람 권한 없을 수 있음 */ }
                }
            }

            // 2) 표본 컬럼 결정 — CLASSES 의 RAW 컬럼(암호화/원문 후보)을 동적으로 찾는다.
            //    한글이 든 _K 컬럼을 포함해야 문자셋 정확도까지 제대로 검증된다.
            var sampleCols = new List<string>();
            try
            {
                await using var cc = cn.CreateCommand();
                cc.CommandText =
                    "SELECT column_name FROM user_tab_columns " +
                    "WHERE table_name='CLASSES' AND data_type='RAW' ORDER BY column_id";
                await using var cr = await cc.ExecuteReaderAsync(ct);
                while (await cr.ReadAsync(ct)) sampleCols.Add(Convert.ToString(cr.GetValue(0)) ?? "");
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            if (sampleCols.Count == 0) sampleCols.Add("CLASS_NAME");
            log("  표본 컬럼: " + string.Join(", ", sampleCols));

            // 3) 후보 복호화/디코드 식 구성 — 무엇이 맞는지 미리 알 수 없으므로 넓게 깔고 점수로 고른다.
            //    (가) 문자셋 디코드: 이름이 암호화가 아니라 레거시 코드페이지 RAW 로 저장된 경우.
            //         현장마다 코드페이지가 다를 수 있어 CP949/EUC-KR/UTF8/Latin 을 모두 시도한다.
            //    (나) 발견된 복호화 함수: all_objects 에서 찾은 DECRYPT/CRYPT/DES 이름 함수.
            //    (다) 관례적 DES_DECRYPT.
            var templates = new List<string>();
            foreach (var cs in new[] { "KO16MSWIN949", "KO16KSC5601", "AL32UTF8", "WE8MSWIN1252" })
                templates.Add($"UTL_I18N.RAW_TO_CHAR({{col}}, '{cs}')");
            templates.Add("UTL_RAW.CAST_TO_VARCHAR2({col})");   // DB 문자셋 그대로 캐스팅

            // 레거시 실측(USPASK_CDMU): 이름은 스키마 패키지 CRYPTO 의 DECRYPT 로 푼다.
            // all_objects 엔 패키지만 보이고 DECRYPT 는 서브프로그램이라, 후보 나열만으론
            // 'CRYPTO.DECRYPT(col)' 을 못 만든다 → 알려진 패턴을 우선 후보로 직접 넣는다.
            templates.Add("CRYPTO.DECRYPT({col})");
            templates.Add("UTL_I18N.RAW_TO_CHAR(CRYPTO.DECRYPT({col}), 'AL32UTF8')");
            templates.Add("UTL_I18N.RAW_TO_CHAR(CRYPTO.DECRYPT({col}), 'KO16MSWIN949')");
            // ★ RAW(200) 저장인 경우: CRYPTO.DECRYPT 입력을 먼저 VARCHAR2 로 캐스팅해야 한다.
            //    (RAW 를 직접 넘기면 암묵 RAW→HEX 문자열 변환이 일어나 복호가 깨진다)
            templates.Add("CRYPTO.DECRYPT(UTL_RAW.CAST_TO_VARCHAR2({col}))");

            foreach (var (owner, name) in candidates)
            {
                // 함수(standalone)일 수도, 패키지일 수도 있으므로 두 형태 모두 시도한다.
                templates.Add($"{owner}.{name}({{col}})");
                templates.Add($"UTL_RAW.CAST_TO_VARCHAR2({owner}.{name}({{col}}))");
                templates.Add($"UTL_I18N.RAW_TO_CHAR({owner}.{name}({{col}}), 'KO16MSWIN949')");
                templates.Add($"{name}({{col}})");
                // 패키지.함수(DECRYPT) 형태
                templates.Add($"{owner}.{name}.DECRYPT({{col}})");
                templates.Add($"{name}.DECRYPT({{col}})");
                templates.Add($"UTL_I18N.RAW_TO_CHAR({name}.DECRYPT({{col}}), 'AL32UTF8')");
                // RAW 저장 대응: 입력을 VARCHAR2 로 캐스팅한 형태
                templates.Add($"{owner}.{name}.DECRYPT(UTL_RAW.CAST_TO_VARCHAR2({{col}}))");
                templates.Add($"{name}.DECRYPT(UTL_RAW.CAST_TO_VARCHAR2({{col}}))");
            }
            templates.Add("DES_DECRYPT({col})");
            templates.Add("UTL_RAW.CAST_TO_VARCHAR2(DES_DECRYPT({col}))");

            // 4) 각 식을 표본(여러 컬럼 × 여러 행)에 적용해 판독 점수를 매기고 최고점을 채택한다.
            //    결과 품질로만 판정하므로 — 문자셋이든 복호화 함수든 DES든 — 방식에 상관없이 동적 대응된다.
            string? best = null; double bestScore = 0; string bestSample = "";
            foreach (var t in templates.Distinct())
            {
                try
                {
                    var exprs = sampleCols.Select(c =>
                        t.Replace("{col}", DbProvider.QuoteColumn(DbProviderKind.Oracle, c)));
                    await using var cmd = cn.CreateCommand();
                    cmd.CommandText = $"SELECT {string.Join(", ", exprs)} FROM CLASSES WHERE ROWNUM <= 5";
                    await using var rr = await cmd.ExecuteReaderAsync(ct);

                    double sum = 0; int n = 0; string firstSample = "";
                    while (await rr.ReadAsync(ct))
                        for (int i = 0; i < rr.FieldCount; i++)
                        {
                            if (rr.IsDBNull(i)) continue;
                            var val = (Convert.ToString(rr.GetValue(i)) ?? "").TrimEnd();
                            if (val.Length == 0) continue;
                            sum += ReadabilityScore(val); n++;
                            if (firstSample.Length == 0) firstSample = val;
                        }
                    double score = n == 0 ? 0 : sum / n;
                    log($"  · {t} → 점수 {score:F2}"
                        + (n > 0 ? $"  표본 \"{Trunc(firstSample, 24)}\"" : "  (표본 없음)"));
                    if (score > bestScore) { bestScore = score; best = t; bestSample = firstSample; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { log($"  · {t} → 오류: {Trunc(ex.Message, 50)}"); }
            }

            // 5) 최고점이 임계값 이상이면 채택. 방식(암호화 아님 vs 복호화 함수)을 구분해 안내한다.
            const double threshold = 0.90;
            if (best != null && bestScore >= threshold)
            {
                OracleDecryptTemplate = best;
                bool plain = best.StartsWith("UTL_I18N.RAW_TO_CHAR({col}", StringComparison.Ordinal)
                          || best == "UTL_RAW.CAST_TO_VARCHAR2({col})";
                log($"  ✅ 채택: {best}   (점수 {bestScore:F2}, 표본 \"{Trunc(bestSample, 40)}\")");
                log(plain
                    ? "═══ 암호화가 아니라 RAW 문자셋 저장 — 문자셋 디코드만 적용. 이후 이관에 자동 적용 ═══"
                    : "═══ 복호화 식 발견 — 이후 이관에 자동 적용 ═══");
                return true;
            }

            log($"═══ 추출 실패 — 최고 점수 {bestScore:F2} < {threshold:F2}. " +
                "위 소스 덤프를 보고 복호화 식을 수동 지정해야 할 수 있음 ═══");
            return false;
        }

        private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name; p.Value = value; cmd.Parameters.Add(p);
        }

        // ─────────────────────────────────────────────────────────────
        //  2) 키 개폐
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// SELECT 전에 호출. 대칭키는 세션(연결) 단위로 열리므로
        /// 같은 연결에서 조회를 마칠 때까지 열어 둔다.
        /// </summary>
        public async Task OpenKeyAsync(DbConnection cn, CancellationToken ct = default)
        {
            // OPEN SYMMETRIC KEY 는 MsSQL 전용 T-SQL 이다. Oracle 소스는 복호화/캐스팅을
            // SELECT 식(OracleDecryptTemplate)에 인라인으로 넣으므로 세션 키 개폐가 없다.
            // (가드 없이 Oracle 연결에 실행하면 ORA-00900 이 난다.)
            if (SourceKind == DbProviderKind.Oracle) return;
            if (Mode != DecryptMode.SymmetricKey) return;
            await OpenKeyCoreAsync(cn, ct);
        }

        public async Task CloseKeyAsync(DbConnection cn, CancellationToken ct = default)
        {
            if (SourceKind == DbProviderKind.Oracle) return;
            if (Mode != DecryptMode.SymmetricKey) return;
            await CloseKeyCoreAsync(cn, ct);
        }

        /// <summary>Mode 와 무관하게 키를 연다. 능력 판정(ProbeAsync) 중에는 이쪽을 써야 한다.</summary>
        private async Task OpenKeyCoreAsync(DbConnection cn, CancellationToken ct) =>
            await ExecAsync(cn,
                $"OPEN SYMMETRIC KEY {SymmetricKeyName} DECRYPTION BY CERTIFICATE {CertificateName};", ct);

        private async Task CloseKeyCoreAsync(DbConnection cn, CancellationToken ct)
        {
            try { await ExecAsync(cn, $"CLOSE SYMMETRIC KEY {SymmetricKeyName};", ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* 이미 닫혔거나 연결이 끊긴 경우 무시 */ }
        }

        // ─────────────────────────────────────────────────────────────
        //  3) SELECT 식 생성
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// SELECT 목록에 넣을 컬럼 식을 만든다.
        /// 암호화 컬럼이고 키가 있으면 복호화 식으로, 아니면 원본 컬럼 그대로 반환한다.
        /// 레거시 FN_Decryption 이 RTRIM 을 걸었으므로 동일하게 맞춘다.
        /// </summary>
        public string SelectExpression(DbProviderKind kind, string table, ColumnInfo col)
        {
            var quoted = DbProvider.QuoteColumn(kind, col.Name);
            // [찾기:복호토글] 복호화 비활성이면 암호화 컬럼이라도 원문 그대로(복호식으로 감싸지 않음).
            if (!DecryptionEnabled || !IsEncrypted(kind, table, col.Name))
                return quoted;

            if (kind == DbProviderKind.Oracle)
            {
                // 발견된 복호화 식({col} 치환)으로 감싼다. 없으면 원본 RAW → 상위(TryResolve)에서 차단.
                if (Mode == DecryptMode.SymmetricKey && !string.IsNullOrWhiteSpace(OracleDecryptTemplate))
                    return OracleDecryptTemplate!.Replace("{col}", quoted) + " AS " + quoted;
                return quoted;
            }

            var spec = EncryptedColumns.First(c =>
                string.Equals(c.Table, table, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Column, col.Name, StringComparison.OrdinalIgnoreCase));

            return Mode == DecryptMode.SymmetricKey
                ? $"RTRIM(CONVERT(varchar({Math.Max(spec.TargetMaxLength, 50)}), DECRYPTBYKEY({quoted}))) AS {quoted}"
                : quoted;   // 값 대체는 ResolveValue 에서 처리
        }

        /// <summary>
        /// 복호화 식만(별칭 없이) 반환한다. 미리보기 전/후 전시에서 별칭을 따로 붙일 때 쓴다.
        /// 복호화 수단이 없으면 원본 컬럼을 그대로 돌려준다.
        /// </summary>
        public string DecryptExpressionOnly(DbProviderKind kind, string table, ColumnInfo col)
        {
            var quoted = DbProvider.QuoteColumn(kind, col.Name);
            // [찾기:복호토글] 복호화 비활성이면 원문 그대로.
            if (!DecryptionEnabled || !IsEncrypted(kind, table, col.Name)) return quoted;

            if (kind == DbProviderKind.Oracle)
                return Mode == DecryptMode.SymmetricKey && !string.IsNullOrWhiteSpace(OracleDecryptTemplate)
                    ? OracleDecryptTemplate!.Replace("{col}", quoted)
                    : quoted;

            var spec = EncryptedColumns.First(c =>
                string.Equals(c.Table, table, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Column, col.Name, StringComparison.OrdinalIgnoreCase));
            return Mode == DecryptMode.SymmetricKey
                ? $"RTRIM(CONVERT(varchar({Math.Max(spec.TargetMaxLength, 50)}), DECRYPTBYKEY({quoted})))"
                : quoted;
        }

        /// <summary>
        /// 암호화 컬럼의 "복호화 전" 원문(암호문/RAW)을 앞 32바이트 헥스로 보여 주는 식.
        /// 미리보기 전/후 전시의 '전' 열에 쓴다.
        /// </summary>
        public static string RawPreviewExpression(DbProviderKind kind, string col)
        {
            var quoted = DbProvider.QuoteColumn(kind, col);
            // 앞 64 헥스문자(=32바이트)만. RAW 전체를 헥스로 바꾼 뒤 문자열로 자르므로
            // 실제 길이보다 크게 요청해 ORA-06502 나는 일이 없다.
            return kind == DbProviderKind.Oracle
                ? $"SUBSTR(RAWTOHEX({quoted}), 1, 64)"
                : $"CONVERT(varchar(max), SUBSTRING({quoted}, 1, 32), 2)";
        }

        /// <summary>현재 적용 중인 복호화 규칙을 사람이 읽을 수 있게 설명한다(UI 전시용).</summary>
        public string RuleDescription()
        {
            if (SourceKind == DbProviderKind.Oracle)
            {
                if (string.IsNullOrWhiteSpace(OracleDecryptTemplate))
                    return "규칙 미발견 — [🔐 Oracle 암호화 추출]을 먼저 실행하세요.";
                bool plain = OracleDecryptTemplate!.StartsWith("UTL_I18N.RAW_TO_CHAR({col}", StringComparison.Ordinal)
                          || OracleDecryptTemplate == "UTL_RAW.CAST_TO_VARCHAR2({col})";
                return $"{OracleDecryptTemplate}"
                     + (plain ? "   (암호화 아님 · RAW→문자 디코드)" : "   (복호화 함수)");
            }

            return Mode == DecryptMode.SymmetricKey
                ? $"RTRIM(CONVERT(varchar, DECRYPTBYKEY(col)))   [대칭키 {SymmetricKeyName} · 인증서 {CertificateName}]"
                : $"대칭키 미개방 — {Diagnosis}";
        }

        /// <summary>
        /// 읽어온 값을 타겟에 넣을 최종 평문으로 변환한다.
        /// SymmetricKey 모드면 이미 평문이므로 그대로, 아니면 대체값을 만든다.
        /// </summary>
        public object? ResolveValue(string table, string column, object? raw, long sourcePk)
        {
            if (!IsEncrypted(table, column)) return raw;

            switch (Mode)
            {
                case DecryptMode.SymmetricKey:
                    // [NAIMS-ISSUE:CHAR-PADDING] 복호 결과도 소스 char(N) 폭만큼 공백이 붙어 온다.
                    //   정규화 규칙은 LegacyText 한 곳에만 둔다(여기서 따로 TrimEnd 하지 않는다).
                    return LegacyText.NormalizeValue(raw);

                case DecryptMode.ExternalMap:
                    if (_externalMap.TryGetValue($"{table}.{column}", out var m)
                        && m.TryGetValue(sourcePk, out var name))
                        return LegacyText.Normalize(name);
                    return $"UNKNOWN_{sourcePk}";

                case DecryptMode.Placeholder:
                    return $"UNKNOWN_{sourcePk}";

                default:
                    throw new InvalidOperationException(
                        $"{table}.{column} 은 암호화 컬럼이다. 복호화 수단이 없어 이관할 수 없다. ({Diagnosis})");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  4) 외부 이름표 주입 (대안 A)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// "소스PK,평문이름" 형식 CSV 를 읽어 특정 컬럼의 이름표로 등록한다.
        /// 레거시 클라이언트 화면에서 뽑아낸 목록을 그대로 쓸 수 있게 CP949/UTF-8 둘 다 허용.
        /// </summary>
        public int LoadExternalMap(string table, string column, string csvPath)
        {
            var map = new Dictionary<long, string>();
            foreach (var line in File.ReadAllLines(csvPath, DetectEncoding(csvPath)))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;
                int comma = t.IndexOf(',');
                if (comma <= 0) continue;
                if (!long.TryParse(t[..comma].Trim(), out var id)) continue;   // 헤더행은 자동 무시
                map[id] = t[(comma + 1)..].Trim().Trim('"');
            }
            _externalMap[$"{table}.{column}"] = map;
            return map.Count;
        }

        private static Encoding DetectEncoding(string path)
        {
            // BOM 판별에는 앞 몇 바이트면 충분 — ReadAllBytes(파일 전체 적재)는 대용량 CSV 에서 낭비라 앞 4바이트만 읽는다.
            var head = new byte[4];
            int n;
            using (var fs = File.OpenRead(path))
                n = fs.Read(head, 0, head.Length);
            if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
                return Encoding.UTF8;
            try { return Encoding.GetEncoding(949); } catch { return Encoding.UTF8; }
        }

        // ─────────────────────────────────────────────────────────────
        //  5) 원본 서버에서 실행할 백업/복원 스크립트
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 이관 계정에 복호화 권한을 부여하는 스크립트. sysadmin 으로 실행한다.
        ///
        /// 2026-07-22 실측: 키·인증서·FN_Decryption 은 원본 DB에 모두 살아 있었고
        /// (key_guid = ExpectedKeyGuid, 복호화 검증 성공), db_datareader 만 가진
        /// NaimsMig 이 메타데이터 가시성 때문에 0건으로 보고 있었을 뿐이다.
        /// 대칭키를 열려면 키에 VIEW DEFINITION, 이를 보호하는 인증서에 CONTROL 이 필요하다.
        /// 마스터키는 서비스 마스터키로 자동 개방되므로 추가 조치가 없다.
        /// </summary>
        public static string GrantScript(string login = "NaimsMig") => $@"
USE NAIMS;
GRANT VIEW DEFINITION ON SYMMETRIC KEY::{SymmetricKeyName} TO {login};
GRANT VIEW DEFINITION ON CERTIFICATE::{CertificateName}    TO {login};
GRANT CONTROL        ON CERTIFICATE::{CertificateName}     TO {login};
GRANT EXECUTE ON [dbo].[FN_Decryption]      TO {login};
GRANT EXECUTE ON [dbo].[Open_Symmetric_Key] TO {login};
".TrimStart();

        /// <summary>
        /// 키가 정말로 없는 환경(사본 DB 등)에서만 필요한 인증서 백업 스크립트.
        /// 통상적으로는 GrantScript() 로 해결된다.
        /// </summary>
        public static string BackupScript(string password = "Mig!2026Temp") => $@"
USE NAIMS;
-- ① 키가 살아 있는지 확인 (key_guid 가 {ExpectedKeyGuid} 여야 함)
SELECT name, key_guid, algorithm_desc FROM sys.symmetric_keys;
SELECT name, subject, expiry_date FROM sys.certificates;

-- ② 실제로 풀리는지 검증
OPEN SYMMETRIC KEY {SymmetricKeyName} DECRYPTION BY CERTIFICATE {CertificateName};
SELECT TOP 10 CLASS_ID, CONVERT(varchar(200), DECRYPTBYKEY(CLASS_NAME)) AS CLASS_NAME FROM CLASSES;
CLOSE SYMMETRIC KEY {SymmetricKeyName};

-- ③ ②가 성공했을 때만 백업
BACKUP CERTIFICATE {CertificateName}
  TO FILE = 'C:\temp\{CertificateName}.cer'
  WITH PRIVATE KEY (
      FILE = 'C:\temp\{CertificateName}.pvk',
      ENCRYPTION BY PASSWORD = '{password}'
  );
".TrimStart();

        /// <summary>백업 파일을 받은 뒤 작업용 DB에 복원하는 스크립트.</summary>
        public static string RestoreScript(string cerPath, string pvkPath, string password = "Mig!2026Temp") => $@"
USE NAIMS;
IF NOT EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE name = '##MS_DatabaseMasterKey##')
    CREATE MASTER KEY ENCRYPTION BY PASSWORD = '{password}';

CREATE CERTIFICATE {CertificateName}
  FROM FILE = '{cerPath}'
  WITH PRIVATE KEY (FILE = '{pvkPath}', DECRYPTION BY PASSWORD = '{password}');

-- 주의: 대칭키는 원본과 동일한 KEY_SOURCE / IDENTITY_VALUE 로 만들어야
--       key_guid 가 같아지고 기존 암호문이 풀린다.
--       (원본 CREATE SYMMETRIC KEY 문 확보 필요)
-- CREATE SYMMETRIC KEY {SymmetricKeyName}
--   WITH ALGORITHM = AES_256,
--        KEY_SOURCE = '<원본과 동일>',
--        IDENTITY_VALUE = '<원본과 동일>'
--   ENCRYPTION BY CERTIFICATE {CertificateName};
".TrimStart();

        // ── helpers ──
        private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "...";

        private static async Task ExecAsync(DbConnection cn, string sql, CancellationToken ct)
        {
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task<object?> ScalarAsync(
            DbConnection cn, string sql, (string, object)? param, CancellationToken ct)
        {
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = sql;
            if (param is { } p)
            {
                var dp = cmd.CreateParameter();
                dp.ParameterName = p.Item1;
                dp.Value = p.Item2;
                cmd.Parameters.Add(dp);
            }
            var v = await cmd.ExecuteScalarAsync(ct);
            return v == DBNull.Value ? null : v;
        }
    }
}
