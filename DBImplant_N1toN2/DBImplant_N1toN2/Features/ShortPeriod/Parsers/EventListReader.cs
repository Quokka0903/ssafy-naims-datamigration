using System;  // System 네임스페이스를 사용
using System.Collections.Generic;  // List 컬렉션을 사용하기 위한 네임스페이스를 포함
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;  // 파일 읽기/쓰기를 위한 네임스페이스를 포함
using System.Linq;  // LINQ 관련 기능을 사용하기 위한 네임스페이스를 포함
using System.Text;  // 문자열 인코딩 관련 기능을 사용하기 위한 네임스페이스를 포함
using System.Threading.Tasks;
using System.Windows.Shapes;
using static DBImplant_N1toN2.Features.ShortPeriod.Parsers.SdLoader;

namespace DBImplant_N1toN2.Features.ShortPeriod.Parsers
{
    public class EventListReader  // `EventListReader` 클래스를 정의
    {        
        // Header 클래스 정의
        public class Header
        {
            public string RawAcousticDataFileName;  // 원음 자료명
            public string DetectSystem;  // 대체 탐지 함 이름
            public string DetectShipName;  // 탐지 함 이름
            public string DetectSonar;  // 탐지 소나
            public string DetectDate;  // 탐지 일자
            public string AnalyzerID;  // 분석자 ID
            public string ClassificationEquipment;  // 분류 장비
        }

        public struct SHORT_PERIOD_PARAMETER
        {
            public double m_dDTThreshold;  // DT 임계값
            public double m_dBWThreshold;  // BW 임계값
        }

        // 이벤트 목록을 파일에서 읽어오는 메소드 정의
        /// <summary>
        ///  Short-period *.lst* 파서 (TR/AI/NB 공용)
        /// </summary>
        /// <param name="filePath">*.lst* 전체 경로</param>
        /// <param name="header">헤더 메타가 채워져서 반환</param>
        /// <param name="isTR">
        ///     true  : 세그먼트마다 PatternValue + FrameList 두 줄을 추가로 읽음(TR 포맷)<br/>
        ///     false : 한 줄짜리 세그먼트만 읽음(AI/NB 포맷)<br/>
        ///     null  : 첫 이벤트 DetectingType 이 “TR” 인지 자동 판별
        /// </param>
        public static ObservableCollection<ShortPeriodEvent> ReadEventListFile(
            string filePath,
            out Header header,
            bool? isTR = null)
        {
            var events = new ObservableCollection<ShortPeriodEvent>();
            header = new Header();

            if (!File.Exists(filePath))
                throw new FileNotFoundException(filePath);

            using var reader = new StreamReader(filePath, Encoding.GetEncoding(949));

            /* ───────────────────── 1. 헤더 / 이벤트 수 ───────────────────── */
            string firstLine = reader.ReadLine() ?? throw new InvalidDataException("Empty file.");

            string eventCountLine;
            if (long.TryParse(firstLine.TrimStart(), out _))          // 헤더 없음
            {
                eventCountLine = firstLine.TrimStart();
            }
            else                                                      // 헤더 존재
            {
                header.RawAcousticDataFileName = firstLine[..80].TrimEnd();
                header.DetectSystem = firstLine.Substring(80, 30).TrimEnd();
                header.DetectShipName = firstLine.Substring(110, 30).TrimEnd();
                header.DetectSonar = firstLine.Substring(140, 10).TrimEnd();
                header.DetectDate = firstLine.Substring(150, 20).TrimEnd();
                header.AnalyzerID = firstLine.Substring(170, 8).TrimEnd();
                header.ClassificationEquipment = firstLine.Substring(178, 8).TrimEnd();

                eventCountLine = reader.ReadLine()?.TrimStart()
                                 ?? throw new InvalidDataException("Missing event-count line.");
            }

            if (!long.TryParse(eventCountLine, out var eventCount))
                throw new FormatException($"Invalid event-count: \"{eventCountLine}\"");

            /* ───────────────────── 2. 이벤트 루프 ───────────────────── */
            for (var evtIdx = 0; evtIdx < eventCount; ++evtIdx)
            {
                string line = reader.ReadLine();
                if (line == null)
                {
                    Debug.WriteLine($"[WARN] EOF at event {evtIdx}/{eventCount}");
                    break;
                }

                var evt = new ShortPeriodEvent
                {
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
                    SnippetComment = SafeSubstr(line, 369, 80).Trim(),   // ← 수정
                    ManualOrAuto = line.Length > 449 && line.Substring(449, Math.Min(2, line.Length - 449)).Trim() == "1"
                };

                /* TR 자동 판정용 */
                bool trFlag = isTR ?? evt.DetectingType.Equals("TR", StringComparison.OrdinalIgnoreCase);

                /* 2-1 세그먼트 수 */
                int segCount = int.TryParse(reader.ReadLine()?.Trim(), out var sc) ? sc : 0;

                /* 2-2 세그먼트 루프 */
                for (var segIdx = 0; segIdx < segCount; ++segIdx)
                {
                    string segLine = reader.ReadLine();
                    if (segLine == null)
                    {
                        Debug.WriteLine($"[WARN] EOF at seg {segIdx}/{segCount}");
                        break;
                    }

                    var seg = new ShortPeriodSegment
                    {
                        SegmentID = (int)ParseInt64(segLine, 0, 8),
                        StartTime = ParseInt64(segLine, 8, 20),
                        EndTime = ParseInt64(segLine, 28, 20),
                        Length = ParseInt64(segLine, 48, 20),
                        DetectingType = SdPackageImporter.DetectTypeToInt(segLine.Substring(68, 3).Trim()),
                        LDAx = ParseDouble(segLine, 71, 16),
                        LDAy = ParseDouble(segLine, 87, 16),
                        StartFreq = ParseDouble(segLine, 103, 16),
                        EndFreq = ParseDouble(segLine, 119, 16),
                        Modulation = int.TryParse(segLine.Substring(135, 4), out var md) ? md : 0,
                        CenterFreq = ParseDouble(segLine, 139, 16),
                        BandWidth = ParseDouble(segLine, 155, 16),
                        SnippetFileName = segLine.Substring(171, 80).Trim(),
                        DBSegID = ParseInt64(segLine, 251, 16),


                    };
                    /* ── DEBUG ───────────────────────────────────────── */
                    if (!string.IsNullOrWhiteSpace(seg.SnippetFileName))
                        Debug.WriteLine($"[LST] Seg#{seg.SegmentID} SnipName = \"{seg.SnippetFileName}\"");
                    /* ────────────────────────────────────────────────── */
                    seg.FrameCount = (int)(seg.Length / 15625);

                    // TR 전용 추가 블록: (옵션 마커 1줄) + PatternValue + Frame 확정정보(토큰 스트림)
                    if (trFlag)
                    {
                        // ② PatternValue (가끔 그 전에 "2" 같은 정수 마커 1줄이 끼는 케이스가 있어 보임)
                        string? l1 = ReadNextNonEmptyLine(reader);
                        if (l1 == null) break;

                        // "2" 같은 짧은 정수 라인 + 다음 라인이 double이면, l1은 마커로 보고 버린다.
                        if (LooksLikeSmallInt(l1, out _))
                        {
                            string? l2 = ReadNextNonEmptyLine(reader);
                            if (l2 == null) break;

                            if (LooksLikeDoubleLine(l2))
                            {
                                seg.PatternValue = ParseDoubleLine(l2);
                            }
                            else
                            {
                                // 예외 케이스: l1이 사실 PatternValue였던 경우
                                seg.PatternValue = ParseDoubleLine(l1);

                                // l2는 프레임 토큰의 시작일 수 있으니 seed로 넣고 진행
                                ReadFrameTriples(reader, seg, seedLine: l2);
                                goto AfterTrBlock;
                            }
                        }
                        else
                        {
                            seg.PatternValue = ParseDoubleLine(l1);
                        }

                        // ③ Frame 확정정보: 줄바꿈 상관없이 토큰으로 읽음
                        ReadFrameTriples(reader, seg);
AfterTrBlock:;
                    }

                    evt.Segments.Add(seg);
                }
                events.Add(evt);
            }

            return events;
                        
        }

        /* ──────────── 헬퍼 ──────────── */

        static string SafeSubstr(string s, int start, int len)
        {
            if (s.Length <= start) return string.Empty;
            int realLen = Math.Min(len, s.Length - start);
            return s.Substring(start, realLen).TrimEnd();
        }


        static double ParseDoubleLine(string? wholeLine)  // PatternValue·Score 등 “한 줄 전체” 파싱용
            => double.TryParse(wholeLine?.Trim(),
                               NumberStyles.Any,
                               CultureInfo.InvariantCulture,
                               out var d) ? d : 0;


        // CFile::Read 에서 char[n] 을 읽어낼 때와 동일하게,
        // 고정 길이만큼 바이트를 읽고, 널종료 이전까지 자른 뒤 트림합니다.
        private static string ReadFixedString(BinaryReader br, int length, Encoding enc)
        {
            var bytes = br.ReadBytes(length);
            var s = enc.GetString(bytes);
            var idx = s.IndexOf('\0');
            if (idx >= 0) s = s.Substring(0, idx);
            return s.Trim();
        }


        // 단주기 신호처리 파라미터를 읽어오는 함수
        public static bool ReadSSParameter(string filePath, ref SHORT_PERIOD_PARAMETER parameter)
        {
            if (!File.Exists(filePath))  // 파일이 존재하는지 확인합니다.
                return false;

            using (StreamReader reader = new StreamReader(filePath, Encoding.GetEncoding(949)))  // 파일을 읽기 모드로 엽니다.
            {
                try
                {
                    // 파일에서 파라미터 값들을 읽어옵니다.
                    parameter.m_dDTThreshold = double.Parse(reader.ReadLine().Trim());
                    parameter.m_dBWThreshold = double.Parse(reader.ReadLine().Trim());

                    return true;  // 읽기 성공
                }
                catch (Exception ex)
                {
                    // 예외 처리: 파일 읽기 실패 또는 형식 오류
                    Console.WriteLine("파일 읽기 오류: " + ex.Message);
                    return false;
                }
            }
        }
        static string? ReadNextNonEmptyLine(StreamReader r)
        {
            while (true)
            {
                var line = r.ReadLine();
                if (line == null) return null;
                if (!string.IsNullOrWhiteSpace(line)) return line;
            }
        }

        static long ParseInt64(string s, int start, int len)
        {
            if (start >= s.Length) return 0;
            int realLen = Math.Min(len, s.Length - start);
            return long.TryParse(s.AsSpan(start, realLen).Trim(), out var v) ? v : 0;
        }

        static double ParseDouble(string s, int start, int len)
        {
            if (start >= s.Length) return 0;
            int realLen = Math.Min(len, s.Length - start);
            return double.TryParse(s.AsSpan(start, realLen).Trim(),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }

        static bool LooksLikeSmallInt(string line, out int v)
        {
            v = 0;
            var t = line.Trim();
            // 너무 긴 정수는 여기서 마커로 취급하지 않음(프레임카운트/ID와 혼동 방지)
            return t.Length > 0 && t.Length <= 3 && int.TryParse(t, out v);
        }

        static bool LooksLikeDoubleLine(string line)
        {
            var t = line.Trim();
            // PatternValue 라인(예: 100.0000000)은 보통 '.'를 포함
            if (!t.Contains('.') && !t.Contains('e') && !t.Contains('E')) return false;
            return double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        }

        static void ReadFrameTriples(StreamReader r, ShortPeriodSegment seg, string? seedLine = null)
        {
            var tokens = new List<string>();

            if (!string.IsNullOrWhiteSpace(seedLine))
                tokens.AddRange(SplitTokens(seedLine));

            while (tokens.Count == 0 || !int.TryParse(tokens[0], out _))
            {
                tokens.Clear();
                ReadTokensUntil(r, 1, tokens);
                if (tokens.Count == 0) return;
            }

            if (!int.TryParse(tokens[0], out var frameCnt) || frameCnt <= 0)
                return;

            int need = 1 + frameCnt * 3;
            ReadTokensUntil(r, need, tokens);

            int idx = 1;
            for (int f = 0; f < frameCnt && idx + 2 < tokens.Count; f++)
            {
                seg.Frames.Add(new ShortPeriodFrame
                {
                    FrameID = long.Parse(tokens[idx++]),
                    DBSegID = long.Parse(tokens[idx++]),
                    DBFrmID = long.Parse(tokens[idx++]),
                });
            }
        }

        static List<string> SplitTokens(string line) =>
            line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).ToList();

        static void ReadTokensUntil(StreamReader r, int minTokens, List<string> acc)
        {
            while (acc.Count < minTokens)
            {
                var line = r.ReadLine();
                if (line == null) break;
                var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0) acc.AddRange(parts);
            }
        }
    }
}
