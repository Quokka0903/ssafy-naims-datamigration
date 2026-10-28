using System.IO;
using System.Text;

namespace RawCollectMigrator.Features.ExtractRaw
{
    /// <summary>
    /// 단주기 이벤트/세그먼트 인덱스 파일 파서.
    ///
    /// [NAIMS-ISSUE:SPS-TEXT-COUNT] SPSlist.txt 는 확장자만 .txt 일 뿐 텍스트가 아니다.
    ///   packed 바이너리(이벤트 307B + 세그먼트 64B×N)이므로 "줄 수 = 이벤트 수" 가 성립하지 않는다.
    ///   예전에는 여기서 비어있지 않은 줄을 세어 목록에 표시했고, 그 값이 실제 이벤트 수와
    ///   전혀 달라 "완비 판정은 통과했는데 이관 건수가 딴판" 인 상태를 만들었다.
    ///   지금은 실제 이관과 같은 파서(SpsListReader)로 세어, 화면 숫자와 이관 결과가 일치한다.
    ///
    /// 이벤트리스트.lst : 이벤트 헤더 라인 + 하위 세그먼트 라인들 (이쪽은 실제 텍스트).
    ///   라인 구조 기반 근사 카운트를 제공한다.
    /// </summary>
    public static class SdIndexParser
    {
        public sealed record SdIndexStats(
            int SpsEventCount,          // SPSlist.txt 이벤트 수 (packed 바이너리 파서 기준)
            int LstLineCount,
            int LstEventLineCount,      // 헤더로 판단되는 라인(들여쓰기 없음 & 숫자·마커 시작)
            int LstSegmentLineCount     // 나머지 = 세그먼트로 취급되는 라인
        );

        public static SdIndexStats Count(string? spsPath, string? lstPath)
        {
            int spsEvents = 0, lstLines = 0, lstEvents = 0, lstSegs = 0;

            // 이관 실행기와 같은 파서를 쓴다 — 목록에 보이는 수와 실제 이관 건수가 어긋나지 않게.
            // 파싱이 도중에 끊기면(error != null) 거기까지 읽힌 건수를 그대로 보여준다.
            // 여기서 예외를 던지면 스캔 전체가 멈추므로, 판정은 이관 시점의 SpsListReader 에 맡긴다.
            if (!string.IsNullOrEmpty(spsPath) && File.Exists(spsPath))
                spsEvents = SpsListReader.Read(spsPath).events.Count;

            if (!string.IsNullOrEmpty(lstPath) && File.Exists(lstPath))
            {
                lstLines = CountLstStructure(lstPath, out lstEvents, out lstSegs);
            }

            return new SdIndexStats(spsEvents, lstLines, lstEvents, lstSegs);
        }


        /// <summary>
        /// 이벤트리스트.lst 라인 구조 카운트.
        /// - 앞에 공백/탭이 없고 (숫자로 시작 OR "이벤트"/"Event" 포함) → 이벤트 헤더 라인
        /// - 그 외 비어있지 않은 라인 → 세그먼트 라인
        /// </summary>
        private static int CountLstStructure(string path, out int eventLines, out int segLines)
        {
            eventLines = 0;
            segLines = 0;
            int total = 0;
            try
            {
                using var sr = new StreamReader(path, DetectEncoding(path));
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    total++;

                    bool leadingWhitespace = line.Length > 0 && (line[0] == ' ' || line[0] == '\t');
                    string trimmed = line.TrimStart();
                    bool looksLikeEventHeader =
                        !leadingWhitespace &&
                        (char.IsDigit(trimmed[0])
                         || trimmed.StartsWith("이벤트", StringComparison.Ordinal)
                         || trimmed.StartsWith("Event", StringComparison.OrdinalIgnoreCase)
                         || trimmed.StartsWith("EVT",   StringComparison.OrdinalIgnoreCase));

                    if (looksLikeEventHeader) eventLines++;
                    else segLines++;
                }
            }
            catch { }
            return total;
        }

        /// <summary>파일 앞부분 N 라인 미리보기(EUC-KR/UTF-8 자동 감지).</summary>
        public static string PreviewHead(string? path, int maxLines = 40, int maxCharsPerLine = 200)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
            try
            {
                using var sr = new StreamReader(path, DetectEncoding(path));
                var sb = new StringBuilder();
                for (int i = 0; i < maxLines; i++)
                {
                    var line = sr.ReadLine();
                    if (line == null) break;
                    if (line.Length > maxCharsPerLine) line = line.Substring(0, maxCharsPerLine) + " …";
                    sb.AppendLine(line);
                }
                return sb.ToString();
            }
            catch (Exception ex) { return "(파일 읽기 실패: " + ex.Message + ")"; }
        }

        /// <summary>텍스트 파일이 UTF-8 BOM 있으면 UTF-8, 아니면 CP949(EUC-KR) 로 취급 — 국내 도구 관례.</summary>
        private static Encoding DetectEncoding(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Length >= 3)
                {
                    Span<byte> bom = stackalloc byte[3];
                    int read = fs.Read(bom);
                    if (read == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                        return Encoding.UTF8;
                }
            }
            catch { }
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(949);
            }
            catch { return Encoding.UTF8; }
        }
    }
}
