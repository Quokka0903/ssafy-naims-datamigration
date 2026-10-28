using System.Globalization;
using System.IO;
using System.Text;

namespace RawCollectMigrator.Features.ExtractRaw
{
    /// <summary>
    /// 이벤트리스트.lst (텍스트, 고정폭, EUC-KR). Main 프로젝트 EventListReader 이식.
    /// </summary>
    public static class EventListReader
    {
        /// <summary>
        /// 정적 생성자. EUC-KR/CP949(코드페이지 949) 인코딩을 사용하기 위해
        /// .NET 8에 기본 포함되지 않는 <see cref="CodePagesEncodingProvider"/>를 전역 등록한다.
        /// 이 등록이 있어야 <c>Encoding.GetEncoding(949)</c> 호출이 예외 없이 동작한다.
        /// </summary>
        static EventListReader() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }

        /// <summary>
        /// 이벤트리스트(.lst) 파일을 읽어 헤더와 이벤트/세그먼트 목록으로 파싱한다.
        /// 포맷은 레거시 C++ AAS가 생성한 텍스트 기반 고정폭(fixed-width) 레코드이며,
        /// 인코딩은 EUC-KR/CP949(코드페이지 949)로 한글 필드(함명, 코멘트 등)를 담는다.
        /// 라인 구조:
        ///  1) 첫 라인 — 숫자만 있으면 이벤트 수(count)로 간주하여 헤더가 없는 파일로 처리하고,
        ///     그렇지 않으면 고정폭 헤더 라인으로 파싱한다(파일명/시스템/함명/소나/일자/분석관 등).
        ///  2) 헤더가 있었다면 다음 라인이 이벤트 수(count).
        ///  3) 이벤트 count만큼: 고정폭 이벤트 라인 → 세그먼트 수 라인 → 세그먼트 count만큼 고정폭 세그먼트 라인.
        ///     TR(추적) 이벤트의 세그먼트 뒤에는 패턴값/프레임 트리플 블록이 이어지며 열람 전용이므로 스킵한다.
        /// 파일이 없으면 빈 결과를 반환한다. 파싱 중 예외·구조 손상(선언된 이벤트/세그먼트 수보다 파일이
        /// 먼저 끝남, TR 프레임 블록 비정상 등)은 삼키지 않고 반환 튜플의 error 문자열에 담아 호출부가
        /// "파싱 실패"를 항목 오류로 표기할 수 있게 한다 — 손상 파일이 부분 결과로 "완료" 위장되는 것 방지.
        /// </summary>
        /// <remarks>
        /// 고정폭 오프셋 맵(문자 기준 시작/폭):
        /// [헤더 라인] 0/80 원음향데이터파일명, 80/30 탐지시스템, 110/30 탐지함명, 140/10 소나,
        ///   150/20 탐지일자, 170/8 분석관ID, 178/8 분류장비.
        /// [이벤트 라인] 0/8 EventID, 8/20 StartTime, 28/20 EndTime, 48/20 EventLength, 68/3 DetectingType,
        ///   71/20 PRI, 91/10 TRMMode, 101/16 SPL, 117/12 Modulation, 129/80 Comment,
        ///   209/80 GramSnippetFileName, 289/80 AudioSnippetFileName, 369/80 SnippetComment,
        ///   449/2 ManualOrAuto("1"이면 수동).
        /// [세그먼트 라인] 0/8 SegmentID, 8/20 StartTime, 28/20 EndTime, 48/20 Length, 68/3 DetectingType(AI/TR),
        ///   71/16 LDAx, 87/16 LDAy, 103/16 StartFreq, 119/16 EndFreq, 135/4 Modulation, 139/16 CenterFreq,
        ///   155/16 BandWidth, 171/80 SnippetFileName, 251/16 DBSegID.
        /// 시간값(StartTime/EndTime/Length)의 단위는 레거시와 동일하며, 세그먼트 FrameCount는 Length/15625로 산출한다.
        /// </remarks>
        /// <param name="filePath">읽을 이벤트리스트(.lst) 파일의 전체 경로.</param>
        /// <param name="isTR">
        /// TR(추적) 이벤트 여부를 강제 지정하는 오버라이드.
        /// null이면 이벤트의 DetectingType이 "TR"인지로 자동 판별한다.
        /// </param>
        /// <returns>
        /// 파싱된 헤더(<see cref="LstHeader"/>)·이벤트 목록(<see cref="SdEvent"/>)·오류 문자열의 튜플.
        /// error 가 null 이면 성공(파일 부재·빈 파일 포함), null 이 아니면 파싱 실패이며
        /// events 에는 실패 시점까지의 부분 결과만 담겨 있으므로 이관 등에 그대로 쓰면 안 된다.
        /// </returns>
        public static (LstHeader header, List<SdEvent> events, string? error) Read(string filePath, bool? isTR = null)
        {
            var header = new LstHeader();
            var events = new List<SdEvent>();
            string? error = null;
            if (!File.Exists(filePath)) return (header, events, null);

            try
            {
                using var reader = new StreamReader(filePath, Encoding.GetEncoding(949));

                string? firstLine = reader.ReadLine();
                if (firstLine == null) return (header, events, null);

                string eventCountLine;
                if (long.TryParse(firstLine.TrimStart(), out _))
                {
                    eventCountLine = firstLine.TrimStart();
                }
                else
                {
                    header.RawAcousticDataFileName = SafeSubstr(firstLine, 0, 80).TrimEnd();
                    header.DetectSystem             = SafeSubstr(firstLine, 80, 30).TrimEnd();
                    header.DetectShipName           = SafeSubstr(firstLine, 110, 30).TrimEnd();
                    header.DetectSonar              = SafeSubstr(firstLine, 140, 10).TrimEnd();
                    header.DetectDate               = SafeSubstr(firstLine, 150, 20).TrimEnd();
                    header.AnalyzerID               = SafeSubstr(firstLine, 170, 8).TrimEnd();
                    header.ClassificationEquipment  = SafeSubstr(firstLine, 178, 8).TrimEnd();

                    eventCountLine = reader.ReadLine()?.TrimStart() ?? "";
                }

                if (!long.TryParse(eventCountLine, out var eventCount))
                    return (header, events, $"이벤트 수 라인이 숫자가 아님: \"{eventCountLine}\"");

                for (int evtIdx = 0; evtIdx < eventCount; evtIdx++)
                {
                    string? line = reader.ReadLine();
                    if (line == null)
                    {
                        // 선언된 이벤트 수보다 파일이 먼저 끝남 — 절단/손상 파일
                        error = $"이벤트 {evtIdx + 1}/{eventCount} 읽는 중 EOF (파일 절단 의심)";
                        break;
                    }

                    var evt = new SdEvent
                    {
                        Index = evtIdx,
                        EventID = ParseInt64(line, 0, 8),
                        StartTime = ParseInt64(line, 8, 20),
                        EndTime = ParseInt64(line, 28, 20),
                        EventLength = ParseInt64(line, 48, 20),
                        DetectingType = SafeSubstr(line, 68, 3),
                        PRI = ParseInt64(line, 71, 20),
                        TRMMode = SafeSubstr(line, 91, 10),
                        SPL = ParseDouble(line, 101, 16),
                        Modulation = SafeSubstr(line, 117, 12),
                        Comment = SafeSubstr(line, 129, 80),
                        GramSnippetFileName = SafeSubstr(line, 209, 80).Trim(),
                        AudioSnippetFileName = SafeSubstr(line, 289, 80).Trim(),
                        SnippetComment = SafeSubstr(line, 369, 80).Trim(),
                        ManualOrAuto = line.Length > 449
                            && line.Substring(449, Math.Min(2, line.Length - 449)).Trim() == "1"
                    };

                    bool trFlag = isTR ?? evt.DetectingType.Equals("TR", StringComparison.OrdinalIgnoreCase);

                    int segCount = int.TryParse(reader.ReadLine()?.Trim(), out var sc) ? sc : 0;
                    evt.SegmentCount = segCount;

                    for (int segIdx = 0; segIdx < segCount; segIdx++)
                    {
                        string? segLine = reader.ReadLine();
                        if (segLine == null)
                        {
                            error = $"이벤트 {evt.EventID}: 세그먼트 {segIdx + 1}/{segCount} 읽는 중 EOF (파일 절단 의심)";
                            break;
                        }

                        var seg = new SdSegment
                        {
                            Index = segIdx,
                            SegmentID = (int)ParseInt64(segLine, 0, 8),
                            StartTime = ParseInt64(segLine, 8, 20),
                            EndTime = ParseInt64(segLine, 28, 20),
                            Length = ParseInt64(segLine, 48, 20),
                            DetectingType = DetectTypeToInt(SafeSubstr(segLine, 68, 3).Trim()),
                            LDAx = ParseDouble(segLine, 71, 16),
                            LDAy = ParseDouble(segLine, 87, 16),
                            StartFreq = ParseDouble(segLine, 103, 16),
                            EndFreq = ParseDouble(segLine, 119, 16),
                            Modulation = int.TryParse(SafeSubstr(segLine, 135, 4), out var md) ? md : 0,
                            CenterFreq = ParseDouble(segLine, 139, 16),
                            BandWidth = ParseDouble(segLine, 155, 16),
                            SnippetFileName = SafeSubstr(segLine, 171, 80).Trim(),
                            DBSegID = ParseInt64(segLine, 251, 16),
                        };
                        seg.FrameCount = (int)(seg.Length / 15625);

                        if (trFlag)
                        {
                            var l1 = ReadNextNonEmptyLine(reader);
                            if (l1 == null)
                            {
                                error = $"이벤트 {evt.EventID}: TR 블록 읽는 중 EOF (파일 절단 의심)";
                                break;
                            }

                            if (LooksLikeSmallInt(l1))
                            {
                                var l2 = ReadNextNonEmptyLine(reader);
                                if (l2 == null)
                                {
                                    error = $"이벤트 {evt.EventID}: TR 블록 읽는 중 EOF (파일 절단 의심)";
                                    break;
                                }
                                if (LooksLikeDoubleLine(l2)) seg.PatternValue = ParseDoubleLine(l2);
                                else
                                {
                                    seg.PatternValue = ParseDoubleLine(l1);
                                    if (!SkipFrameTriples(reader, l2, out var frErr))
                                    {
                                        error = $"이벤트 {evt.EventID}: {frErr}";
                                        break;
                                    }
                                    goto AfterTr;
                                }
                            }
                            else seg.PatternValue = ParseDoubleLine(l1);

                            if (!SkipFrameTriples(reader, null, out var frErr2))
                            {
                                error = $"이벤트 {evt.EventID}: {frErr2}";
                                break;
                            }
                            AfterTr:;
                        }

                        evt.Segments.Add(seg);
                    }

                    if (error != null) break;   // 세그먼트/TR 블록 손상 — 이벤트 루프도 중단

                    // 세그먼트 타입별 재집계
                    evt.AiSegmentCount = evt.Segments.Count(s => s.DetectingType == 1);
                    evt.TrSegmentCount = evt.Segments.Count(s => s.DetectingType == 2);
                    evt.UnkSegmentCount = evt.Segments.Count(s => s.DetectingType != 1 && s.DetectingType != 2);

                    events.Add(evt);
                }
            }
            catch (Exception ex)
            {
                // 예외를 삼켜 부분 결과를 성공처럼 돌려주지 않는다 — 호출부가 항목 오류로 표기.
                error = $"[{ex.GetType().Name}] {ex.Message}";
            }
            return (header, events, error);
        }

        /// <summary>
        /// 세그먼트 탐지 유형 문자열을 내부 정수 코드로 변환한다.
        /// "AI"→1(자동식별), "TR"→2(추적), 그 외/공백→0(미상).
        /// </summary>
        /// <param name="s">고정폭에서 잘라낸 탐지 유형 문자열(예: "AI", "TR").</param>
        /// <returns>탐지 유형 정수 코드(1=AI, 2=TR, 0=미상).</returns>
        private static int DetectTypeToInt(string s) => s switch
        {
            "AI" => 1,
            "TR" => 2,
            _ => 0
        };

        /// <summary>
        /// 고정폭 라인에서 [start, start+len) 구간을 안전하게 잘라내고 끝쪽 공백을 제거한다.
        /// 라인 길이가 start 이하이면 빈 문자열을, 라인이 짧아 len을 채우지 못하면 남은 길이만큼만 반환하여
        /// IndexOutOfRange 예외 없이 가변 길이/절단된 레거시 라인을 처리한다.
        /// </summary>
        /// <param name="s">원본 고정폭 라인.</param>
        /// <param name="start">추출 시작 오프셋(문자 기준).</param>
        /// <param name="len">추출할 필드 폭(문자 수).</param>
        /// <returns>잘라낸 문자열(끝 공백 제거). 범위를 벗어나면 빈 문자열.</returns>
        private static string SafeSubstr(string s, int start, int len)
        {
            if (s.Length <= start) return string.Empty;
            int realLen = Math.Min(len, s.Length - start);
            return s.Substring(start, realLen).TrimEnd();
        }

        /// <summary>
        /// 고정폭 라인의 [start, start+len) 구간을 정수(long)로 파싱한다.
        /// 범위를 벗어나거나 파싱에 실패하면(공백/비숫자 등) 예외 없이 0을 반환한다.
        /// </summary>
        /// <param name="s">원본 고정폭 라인.</param>
        /// <param name="start">추출 시작 오프셋(문자 기준).</param>
        /// <param name="len">추출할 필드 폭(문자 수).</param>
        /// <returns>파싱된 long 값. 실패 시 0.</returns>
        private static long ParseInt64(string s, int start, int len)
        {
            if (start >= s.Length) return 0;
            int realLen = Math.Min(len, s.Length - start);
            return long.TryParse(s.AsSpan(start, realLen).Trim(), out var v) ? v : 0;
        }

        /// <summary>
        /// 고정폭 라인의 [start, start+len) 구간을 실수(double)로 파싱한다.
        /// 소수점 표기는 로캘 무관하게 <see cref="CultureInfo.InvariantCulture"/>('.' 소수점)로 해석하며,
        /// 지수 표기 등 <see cref="NumberStyles.Any"/>를 허용한다.
        /// 범위를 벗어나거나 파싱에 실패하면 예외 없이 0을 반환한다.
        /// </summary>
        /// <param name="s">원본 고정폭 라인.</param>
        /// <param name="start">추출 시작 오프셋(문자 기준).</param>
        /// <param name="len">추출할 필드 폭(문자 수).</param>
        /// <returns>파싱된 double 값. 실패 시 0.</returns>
        private static double ParseDouble(string s, int start, int len)
        {
            if (start >= s.Length) return 0;
            int realLen = Math.Min(len, s.Length - start);
            return double.TryParse(s.AsSpan(start, realLen).Trim(),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }

        /// <summary>
        /// 고정폭이 아닌 한 줄 전체를 하나의 실수(double)로 파싱한다(TR 패턴값 라인 등).
        /// <see cref="CultureInfo.InvariantCulture"/>·<see cref="NumberStyles.Any"/>로 해석하며,
        /// null·공백·파싱 실패 시 예외 없이 0을 반환한다.
        /// </summary>
        /// <param name="wholeLine">파싱할 라인 전체(앞뒤 공백은 Trim).</param>
        /// <returns>파싱된 double 값. 실패 시 0.</returns>
        private static double ParseDoubleLine(string? wholeLine)
            => double.TryParse(wholeLine?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;

        /// <summary>
        /// 스트림에서 공백이 아닌(내용이 있는) 다음 라인이 나올 때까지 읽어 반환한다.
        /// 빈 줄/공백 줄은 건너뛰며, 스트림 끝(EOF)에 도달하면 null을 반환한다.
        /// TR 블록의 패턴값/프레임 라인 사이에 낀 빈 줄을 건너뛰기 위해 사용한다.
        /// </summary>
        /// <param name="r">읽어들일 <see cref="StreamReader"/>.</param>
        /// <returns>내용이 있는 다음 라인. EOF면 null.</returns>
        private static string? ReadNextNonEmptyLine(StreamReader r)
        {
            while (true)
            {
                var line = r.ReadLine();
                if (line == null) return null;
                if (!string.IsNullOrWhiteSpace(line)) return line;
            }
        }

        /// <summary>
        /// 라인이 "작은 정수 한 개"처럼 보이는지 판별한다(Trim 후 길이 1~3자, 정수 파싱 가능).
        /// TR 블록에서 프레임 개수 카운트 라인과 패턴값(실수) 라인을 구분하기 위한 휴리스틱이다.
        /// </summary>
        /// <param name="line">검사할 라인.</param>
        /// <returns>작은 정수 형태이면 true.</returns>
        private static bool LooksLikeSmallInt(string line)
        {
            var t = line.Trim();
            return t.Length > 0 && t.Length <= 3 && int.TryParse(t, out _);
        }

        /// <summary>
        /// 라인이 "실수(double) 한 개"처럼 보이는지 판별한다.
        /// 소수점('.') 또는 지수 표기('e'/'E')를 포함해야 하며(정수와의 오인 방지),
        /// <see cref="CultureInfo.InvariantCulture"/> 기준으로 double 파싱이 가능해야 한다.
        /// TR 블록에서 패턴값 라인과 프레임 카운트 라인을 구분하는 데 사용한다.
        /// </summary>
        /// <param name="line">검사할 라인.</param>
        /// <returns>실수 형태(소수점/지수 포함, 파싱 가능)이면 true.</returns>
        private static bool LooksLikeDoubleLine(string line)
        {
            var t = line.Trim();
            if (!t.Contains('.') && !t.Contains('e') && !t.Contains('E')) return false;
            return double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        }

        /// <summary>
        /// 프레임 개수(frameCnt)의 sanity 상한. 64fps 기준 약 43시간 분량으로 정상 데이터에는 충분히 크고,
        /// 손상 파일의 쓰레기 값(수억 등)이 파일 잔여분을 전부 삼켜 후속 이벤트를 소실시키는 것을 막는다.
        /// </summary>
        private const int MaxFrameCount = 10_000_000;

        /// <summary>TR 프레임 트리플 스킵 — 열람 전용이므로 값은 버림. 성공 여부를 반환한다.</summary>
        /// <remarks>
        /// TR(추적) 세그먼트 뒤에 오는 프레임 데이터 블록을 소비만 하고 버린다.
        /// 블록 첫 토큰이 프레임 개수(frameCnt)이며, 그 뒤로 프레임당 3개(트리플)씩,
        /// 총 (1 + frameCnt*3)개의 공백구분 토큰이 여러 라인에 걸쳐 이어질 수 있다.
        /// seedLine에 이미 읽어둔 라인을 넘기면 그 토큰부터 이어서 소비하며,
        /// 첫 토큰이 정수가 될 때까지 라인을 건너뛴 뒤 필요한 토큰 수만큼 라인을 더 읽는다.
        /// 토큰은 저장하지 않고 개수만 세어 손상 frameCnt 에 의한 메모리 폭증을 차단한다.
        /// 실패(= false + error) 조건: frameCnt 가 <see cref="MaxFrameCount"/> 초과(손상 의심),
        /// 또는 필요한 토큰 수를 채우기 전에 EOF(파일 잔여 토큰 부족).
        /// frameCnt 0 이하이거나 블록 자체가 없으면(EOF) 기존과 같이 조용히 성공 처리한다.
        /// </remarks>
        /// <param name="r">읽어들일 <see cref="StreamReader"/>.</param>
        /// <param name="seedLine">이미 앞서 읽어둔 시작 라인(없으면 null). 해당 토큰부터 소비를 시작한다.</param>
        /// <param name="error">실패 시 원인 문자열, 성공 시 null.</param>
        /// <returns>블록을 정상 소비(또는 블록 없음)하면 true, 손상 감지 시 false.</returns>
        private static bool SkipFrameTriples(StreamReader r, string? seedLine, out string? error)
        {
            error = null;

            string[] tokens = string.IsNullOrWhiteSpace(seedLine)
                ? Array.Empty<string>()
                : seedLine!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            while (tokens.Length == 0 || !int.TryParse(tokens[0], out _))
            {
                var line = r.ReadLine();
                if (line == null) return true;   // 블록 자체가 없음 — 기존과 동일하게 조용히 종료
                tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            }

            if (!int.TryParse(tokens[0], out var frameCnt) || frameCnt <= 0) return true;
            if (frameCnt > MaxFrameCount)
            {
                error = $"TR 프레임 수 비정상({frameCnt:N0} > 상한 {MaxFrameCount:N0}) — 손상 의심";
                return false;
            }

            long need = 1L + (long)frameCnt * 3;
            long have = tokens.Length;
            while (have < need)
            {
                var line = r.ReadLine();
                if (line == null)
                {
                    error = $"TR 프레임 블록 절단 — 토큰 {have:N0}/{need:N0}에서 EOF";
                    return false;
                }
                have += line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            }
            return true;
        }
    }
}
