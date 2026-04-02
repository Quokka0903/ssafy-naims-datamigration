using System;  // System 네임스페이스를 사용
using System.Collections.Generic;  // List 컬렉션을 사용하기 위한 네임스페이스를 포함
using System.Diagnostics;
using System.Globalization;
using System.IO;  // 파일 읽기/쓰기를 위한 네임스페이스를 포함
using System.Linq;  // LINQ 관련 기능을 사용하기 위한 네임스페이스를 포함
using System.Text;  // 문자열 인코딩 관련 기능을 사용하기 위한 네임스페이스를 포함
using System.Threading.Tasks;
using System.Windows.Shapes;
using static Simul_AcousticInfoAnalysis.Import.SdLoader;  // 비동기 작업을 위한 네임스페이스를 포함

namespace Simul_AcousticInfoAnalysis.ReadFileToDB  // 파일을 DB로 읽어오는 클래스를 포함하는 네임스페이스입니다.
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
        public static List<ShortPeriodEvent> ReadEventListFile(
            string filePath,
            out Header header,
            bool? isTR = null)
        {
            var events = new List<ShortPeriodEvent>();
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
                    ManualOrAuto = line.Length >= 451 && line[449..451] == "1"
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

                    /* ─────── TR 전용 추가 두 줄 ─────── */
                    if (trFlag)
                    {
                        /* ② PatternValue */
                        string patternLine = reader.ReadLine();
                        seg.PatternValue = ParseDoubleLine(patternLine);

                        /* ③ FrameList */
                        string frameLine = reader.ReadLine();
                        if (!string.IsNullOrWhiteSpace(frameLine))
                        {
                            var tok = frameLine
                                      .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                            if (int.TryParse(tok[0], out var frameCnt))
                            {
                                int idx = 1;
                                for (int f = 0; f < frameCnt && idx + 2 < tok.Length; f++)
                                {
                                    seg.Frames.Add(new ShortPeriodFrame
                                    {
                                        FrameID = int.Parse(tok[idx++]),
                                        DBSegID = int.Parse(tok[idx++]),
                                        DBFrmID = int.Parse(tok[idx++])
                                    });
                                }
                                if (seg.Frames.Count != frameCnt)
                                    Debug.WriteLine(
                                        $"[WARN] Frame mismatch seg={seg.SegmentID}: header={frameCnt}, parsed={seg.Frames.Count}");
                            }
                        }
                    }
                    /* ──────────────────────────────── */

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

        static long ParseInt64(string s, int start, int len) =>
            long.TryParse(s.AsSpan(start, Math.Min(len, s.Length - start)).Trim(),
                          out var v) ? v : 0;

        static double ParseDouble(string s, int start, int len)
        {
            ReadOnlySpan<char> span = s.AsSpan(start, Math.Min(len, s.Length - start));
            return double.TryParse(span, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
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

        // Event 클래스 정의
        /*public class Event
        {
            public long EventID;  // 이벤트 ID
            public long StartTime;  // 시작 시간
            public long EndTime;  // 종료 시간
            public long EventLength;  // 이벤트 길이
            public string DetectingType;  // 탐지 유형
            public long PRI;  // PRI 값
            public string TRMMode;  // TRM 모드
            public double SPL;  // SPL 값
            public string Modulation;  // 변조 방식
            public string Comment;  // 설명
            public string GramSnippetFileName;  // 그램 스니펫 파일명
            public string AudioSnippetFileName;  // 원음 스니펫 파일명
            public string SnippetComment;  // 스니펫 설명
            public bool ManualOrAuto;  // 수동 또는 자동 여부
            public List<Segment> Segments = new List<Segment>();  // 세그먼트 리스트
        }

        // Segment 클래스 정의
        public class Segment
        {
            public long SegmentID;  // 세그먼트 ID
            public long StartTime;  // 세그먼트 시작 시간
            public long EndTime;  // 세그먼트 종료 시간
            public long Length;  // 세그먼트 길이
            public int FrameCount;  // 프레임 수
            public string DetectingType;  // 탐지 유형
            public double LDAx;  // LDA X 값
            public double LDAy;  // LDA Y 값
            public double StartFreq;  // 시작 주파수
            public double EndFreq;  // 끝 주파수
            public int Modulation;  // 변조 방식
            public double CenterFreq;  // 중심 주파수
            public double BandWidth;  // 대역폭
            public string SnippetFileName;  // 스니펫 파일명
            public long DBSegID;  // DB 세그먼트 ID
            public List<Frame> Frames;  // 프레임 수 (추가 필드, 필요시 사용)
        }

        public class Frame
        {
            public long FrameID;  // 프레임 ID
            public double Frequency;  // 주파수
            public float[] Xi { get; set; }
            public float Score { get; set; }
            public int DBSegmentID { get; set; }  // 예시로 추가된 속성
            public int DBFrameID { get; set; }    // 예시로 추가된 속성
        }*/

    }
}
