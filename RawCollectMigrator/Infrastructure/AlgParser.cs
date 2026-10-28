using System.IO;
using System.Text;

namespace RawCollectMigrator.Infrastructure
{
    /// <summary>
    /// 보조정보.alg 파서. 레거시 AAS 'LOGSHT' 포맷 (UtilFileManage::ReadLogshtFile 기준).
    /// 파일 시작 부분: 임무정보 헤더 — 고정폭 EUC-KR 텍스트.
    ///   offset 0..29   szDetPlatform        (30 bytes) - 진수체계 (탐지플랫폼)
    ///   offset 30..49  szDetVesselClass     (20 bytes) - 진수계급 (체계명) → RECORDING_SYSTEM 매핑 키
    ///   offset 50..69  szDetVesselName      (20 bytes) - 진수함정 (함정명) → RECORDING_VESSEL
    ///   offset 70..77  szMissionDatefr      (8 bytes, YYYYMMDD) → RECORDING_DATETIME
    ///   offset 78..85  szMissionDateto      (8 bytes, YYYYMMDD)
    ///   offset 86..105 szMissionSeaArea     (20 bytes) - 임무해역
    ///   offset 106..185 szMissionSummary    (80 bytes)
    ///   offset 186..265 szMissionComment    (80 bytes)
    ///
    /// ※ 한글은 EUC-KR(CP949)로 인코딩되어 있음.
    /// </summary>
    public sealed class AlgInfo
    {
        public string DetPlatform { get; init; } = "";
        public string DetVesselClass { get; init; } = "";    // 진수 계급 → RECORDING_SYSTEM
        public string DetVesselName { get; init; } = "";     // 진수 함정명 → RECORDING_VESSEL
        public string MissionDateFrom { get; init; } = "";   // YYYYMMDD → RECORDING_DATETIME
        public string MissionDateTo { get; init; } = "";
        public string MissionSeaArea { get; init; } = "";
        public string MissionSummary { get; init; } = "";
        public string MissionComment { get; init; } = "";

        /// <summary>RECORDING_DATETIME 후보. MissionDateFrom 파싱.</summary>
        public DateTime? RecordingDateTime
        {
            get
            {
                if (MissionDateFrom.Length != 8) return null;
                if (!int.TryParse(MissionDateFrom.Substring(0, 4), out int y)) return null;
                if (!int.TryParse(MissionDateFrom.Substring(4, 2), out int mo)) return null;
                if (!int.TryParse(MissionDateFrom.Substring(6, 2), out int d)) return null;
                try { return new DateTime(y, mo, d); } catch { return null; }
            }
        }

        /// <summary>
        /// szDetVesselClass → RECORDING_SYSTEM 매핑.
        /// 레거시 AAS::ModelRawData::UpdateRawDMFfromLogsht(ModelRawData.cpp:2651) 기준 —
        /// AAS 가 만든 자료에서는 이 필드에 <b>체계명</b>이 들어 있어 그대로 매핑이 성립한다.
        ///
        /// ※ CDMU(USPASK) 가 만든 자료에서는 이 필드가 진짜 <b>함정급</b>("214") 이거나 시험 오염값
        ///   ("aaa"/"test"/"OT_옥포")이라 전부 ETC 로 떨어진다. 그런 자료의 수집체계는 자료관리파일.DMF
        ///   [LIST]수집체계 에만 있으므로 호출부가 DMF 를 우선 쓴다
        ///   (ExtractMigrationExecutor 의 [NAIMS-ISSUE:DMF-METADATA] 참조).
        /// </summary>
        public string RecordingSystemMapped => RecordingSystem.Normalize(DetVesselClass);
    }

    /// <summary>
    /// RECORDING_SYSTEM(PREC_SRC_SIG, varchar(30)) 어휘를 한 벌로 통일한다.
    ///
    /// 같은 체계를 .alg 는 "ISUS83"·"USNAVY" 로, DMF 는 "ISUS83-TCIS"·"US Navy" 로 적는다
    /// (DMF 어휘의 정의는 레거시 USPASK_APU\DataManage\DataManageDefine.h:180 DMTEXT_DETPLATFORM_*).
    /// 두 출처가 섞여 들어오므로 컬럼에는 한 가지 표기만 남도록 여기서 정규화한다.
    /// 표기는 기존에 이관된 값을 흔들지 않도록 .alg 매핑이 내던 형태를 정본으로 삼는다.
    /// </summary>
    public static class RecordingSystem
    {
        /// <summary>
        /// .alg szDetVesselClass 또는 DMF [LIST]수집체계 원문을 정본 표기로 바꾼다.
        /// 알 수 없는 값·빈 값은 "ETC".
        /// </summary>
        /// <param name="raw">원문(대소문자·공백 무관).</param>
        /// <returns>정본 RECORDING_SYSTEM 문자열.</returns>
        public static string Normalize(string? raw)
        {
            var s = (raw ?? "").Trim().ToUpperInvariant().Replace(" ", "").Replace("_", "-");
            return s switch
            {
                "ISUS83-TCIS" or "ISUS83TCIS" or "ISUSTCIS" or "ISUS83" => "ISUS83-TCIS",
                "ISUS90"                                                => "ISUS90",
                "JANGBOGO3" or "JANG3"                                  => "JANGBOGO3",
                "FTAS-TCIS" or "FTASTCIS"                               => "FTAS-TCIS",
                "WSD-210K" or "WSD210K"                                 => "WSD-210K",
                "SQR-230K" or "SQR230K"                                 => "SQR-230K",
                "SQR-220K" or "SQR220K"                                 => "SQR-220K",
                "TSM2932S"                                              => "TSM2932S",
                "FAMS"                                                  => "FAMS",
                "P-3C" or "P3C"                                         => "P-3C",
                "ADD" or "ADD-TEC" or "ADDTEC"                          => "ADD",
                "US" or "USNAVY" or "US-NAVY"                           => "US-NAVY",
                _                                                       => "ETC",
            };
        }

        /// <summary>정규화 결과가 실제 체계로 식별됐는지(= ETC 로 떨어지지 않았는지).</summary>
        /// <param name="raw">원문.</param>
        /// <returns>ETC 가 아니면 true.</returns>
        public static bool IsIdentified(string? raw) => Normalize(raw) != "ETC";
    }

    public static class AlgParser
    {
        // EUC-KR 인코딩 1회 등록 (System.Text.Encoding.CodePages 패키지 없어도 .NET 8 Windows에서는 CP949 사용 가능)
        private static readonly Encoding EucKr = GetEucKr();

        private static Encoding GetEucKr()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                // [NAIMS-ISSUE:ALG-CP949] 보조정보.alg 한글 EUC-KR 인코딩
                return Encoding.GetEncoding(949);
            }
            catch
            {
                return Encoding.UTF8;
            }
        }

        public static AlgInfo? TryParse(string algPath)
        {
            try
            {
                if (!File.Exists(algPath)) return null;
                var bytes = File.ReadAllBytes(algPath);
                if (bytes.Length < 266) return null;

                string Take(int offset, int length)
                {
                    int len = Math.Min(length, bytes.Length - offset);
                    if (len <= 0) return "";
                    return EucKr.GetString(bytes, offset, len).TrimEnd('\0', ' ', '\r', '\n').Trim();
                }

                return new AlgInfo
                {
                    DetPlatform     = Take(0, 30),
                    DetVesselClass  = Take(30, 20),
                    DetVesselName   = Take(50, 20),
                    MissionDateFrom = Take(70, 8),
                    MissionDateTo   = Take(78, 8),
                    MissionSeaArea  = Take(86, 20),
                    MissionSummary  = Take(106, 80),
                    MissionComment  = Take(186, 80),
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
