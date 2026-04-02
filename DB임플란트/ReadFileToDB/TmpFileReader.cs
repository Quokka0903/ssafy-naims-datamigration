using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Simul_AcousticInfoAnalysis.ReadFileToDB
{
    // TR 이벤트/세그먼트 결과 후보
    public class TR_EVENT_RESULT
    {
        public long EventID { get; set; }
        public long ConfirmedCount { get; set; }
        public long SegmentCount { get; set; }
        public List<TR_SEGMENT_RESULT_CANDIDATE> listSegmentResult { get; } = new();
    }

    public class TR_SEGMENT_RESULT_CANDIDATE
    {
        public int ConfirmedItem { get; set; }
        public long SegmentID { get; set; }
        public long StartTime { get; set; }
        public long EndTime { get; set; }
        public double Score { get; set; }
        // 필요하면 추가 필드…
    }

    // AI 이벤트/후보/세그먼트 결과 후보
    public class AI_EVENT_RESULT
    {
        public long EventID { get; set; }
        public string ConfirmedSonarName { get; set; } = string.Empty;
        public string ConfirmedPulseName { get; set; } = string.Empty;
        public string ConfirmedComment { get; set; } = string.Empty;
        public float ConfirmedScore { get; set; }
        public int ConfirmMode { get; set; }
        public long ConfirmedDBPulseID { get; set; }
        public List<AI_RESULT_CANDIDATE> listCandidate { get; } = new();
    }

    public class AI_RESULT_CANDIDATE
    {
        public long DBPulseID { get; set; }
        public string PulseName { get; set; } = string.Empty;
        public string SonarName { get; set; } = string.Empty;
        public double Score { get; set; }
        public int Rank { get; set; }
        public int SegmentCount { get; set; }
        public bool IsSelected { get; set; }
        public List<AI_SEGMENT_RESULT> listSegmentResult { get; } = new();
    }

    public class AI_SEGMENT_RESULT
    {
        public long SegmentID { get; set; }
        public long DbSegmentID { get; set; }
        public double Score { get; set; }
        public double StartFreq { get; set; }
        public double EndFreq { get; set; }
        public int Rank { get; set; }    // m_iRank
    }

    public static class TmpFileReader
    {
        private static bool TryRead<T>(BinaryReader br, out T value) where T : struct
        {
            int size = typeof(T) switch
            {
                var t when t == typeof(bool) => 1,
                var t when t == typeof(byte) => 1,
                var t when t == typeof(int) => 4,
                var t when t == typeof(uint) => 4,
                var t when t == typeof(long) => 8,
                var t when t == typeof(float) => 4,
                var t when t == typeof(double) => 8,
                _ => throw new NotSupportedException($"Type {typeof(T)} not supported")
            };

            if (br.BaseStream.Length - br.BaseStream.Position < size)
            {
                value = default!;
                return false;
            }

            object raw = typeof(T) switch
            {
                var t when t == typeof(bool) => br.ReadBoolean(),
                var t when t == typeof(byte) => br.ReadByte(),
                var t when t == typeof(int) => br.ReadInt32(),
                var t when t == typeof(uint) => br.ReadUInt32(),
                var t when t == typeof(long) => br.ReadInt64(),
                var t when t == typeof(float) => br.ReadSingle(),
                var t when t == typeof(double) => br.ReadDouble(),
                _ => default!
            };
            value = (T)raw;
            return true;
        }

        public static List<TR_EVENT_RESULT> ReadTRResultListFile(string filePath)
        {
            var result = new List<TR_EVENT_RESULT>();
            if (!File.Exists(filePath))
            {
                Debug.WriteLine($"[ERR ] TR 파일이 없습니다: {filePath}");
                return result;
            }

            using var br = new BinaryReader(File.Open(filePath, FileMode.Open, FileAccess.Read));
            if (!TryRead<int>(br, out int eventCount))
            {
                Debug.WriteLine("[ERR ] TR 이벤트 개수 읽기 실패");
                return result;
            }

            for (int i = 0; i < eventCount; i++)
            {
                var ev = new TR_EVENT_RESULT();

                // EventID
                if (!TryRead<long>(br, out long tmpEventID))
                {
                    Debug.WriteLine($"[WARN] TR[{i}] EventID 누락, 중단");
                    break;
                }
                ev.EventID = tmpEventID;

                // ConfirmedCount
                if (!TryRead<long>(br, out long tmpConfirmedCount))
                {
                    Debug.WriteLine($"[WARN] TR[{i}] ConfirmedCount 누락, 중단");
                    break;
                }
                ev.ConfirmedCount = tmpConfirmedCount;

                // SegmentCount
                if (!TryRead<long>(br, out long tmpSegmentCount))
                {
                    Debug.WriteLine($"[WARN] TR[{i}] SegmentCount 누락, 중단");
                    break;
                }
                ev.SegmentCount = tmpSegmentCount;

                // 세그먼트 읽기
                for (long j = 0; j < ev.ConfirmedCount; j++)
                {
                    var seg = new TR_SEGMENT_RESULT_CANDIDATE();

                    if (!TryRead<int>(br, out int tmpConfirmedItem))
                    {
                        Debug.WriteLine($"[WARN] TR[{i}] Seg[{j}] ConfirmedItem 누락");
                        break;
                    }
                    seg.ConfirmedItem = tmpConfirmedItem;
                    if (seg.ConfirmedItem == 0)
                        continue;

                    if (!TryRead<long>(br, out long tmpSegmentID))
                    {
                        Debug.WriteLine($"[WARN] TR[{i}] Seg[{j}] SegmentID 누락");
                        break;
                    }
                    seg.SegmentID = tmpSegmentID;

                    if (!TryRead<long>(br, out long tmpStartTime))
                    {
                        Debug.WriteLine($"[WARN] TR[{i}] Seg[{j}] StartTime 누락");
                        break;
                    }
                    seg.StartTime = tmpStartTime;

                    if (!TryRead<long>(br, out long tmpEndTime))
                    {
                        Debug.WriteLine($"[WARN] TR[{i}] Seg[{j}] EndTime 누락");
                        break;
                    }
                    seg.EndTime = tmpEndTime;

                    if (!TryRead<double>(br, out double tmpScore))
                    {
                        Debug.WriteLine($"[WARN] TR[{i}] Seg[{j}] Score 누락");
                        break;
                    }
                    seg.Score = tmpScore;

                    ev.listSegmentResult.Add(seg);
                }

                result.Add(ev);
            }

            return result;
        }

        public static List<AI_EVENT_RESULT> ReadAIResultListFile(string filePath)
        {
            var result = new List<AI_EVENT_RESULT>();
            if (!File.Exists(filePath))
            {
                Debug.WriteLine($"[ERR ] AI 파일이 없습니다: {filePath}");
                return result;
            }

            using var br = new BinaryReader(File.Open(filePath, FileMode.Open, FileAccess.Read));
            if (!TryRead<int>(br, out int eventCount))
            {
                Debug.WriteLine("[ERR ] AI 이벤트 개수 읽기 실패");
                return result;
            }

            for (int i = 0; i < eventCount; i++)
            {
                var ev = new AI_EVENT_RESULT();

                // EventID
                if (!TryRead<long>(br, out long tmpEventID))
                {
                    Debug.WriteLine($"[WARN] AI[{i}] EventID 누락, 중단");
                    break;
                }
                ev.EventID = tmpEventID;

                // 고정 길이 문자열 읽기 (char[N])
                byte[] buf31 = br.ReadBytes(31);
                ev.ConfirmedSonarName = Encoding.Default.GetString(buf31).TrimEnd('\0', ' ');
                byte[] buf31b = br.ReadBytes(31);
                ev.ConfirmedPulseName = Encoding.Default.GetString(buf31b).TrimEnd('\0', ' ');
                byte[] buf81 = br.ReadBytes(81);
                ev.ConfirmedComment = Encoding.Default.GetString(buf81).TrimEnd('\0', ' ');

                // ConfirmedScore
                if (!TryRead<float>(br, out float tmpScore))
                {
                    Debug.WriteLine($"[WARN] AI[{i}] ConfirmedScore 누락");
                    break;
                }
                ev.ConfirmedScore = tmpScore;

                // ConfirmMode
                if (!TryRead<int>(br, out int tmpMode))
                {
                    Debug.WriteLine($"[WARN] AI[{i}] ConfirmMode 누락");
                    break;
                }
                ev.ConfirmMode = tmpMode;

                // ConfirmedDBPulseID
                if (!TryRead<long>(br, out long tmpDBPulseID))
                {
                    Debug.WriteLine($"[WARN] AI[{i}] ConfirmedDBPulseID 누락");
                    break;
                }
                ev.ConfirmedDBPulseID = tmpDBPulseID;

                // 후보 읽기
                if (ev.ConfirmMode == 1 || ev.ConfirmMode == 2)
                {
                    var cand = new AI_RESULT_CANDIDATE();

                    if (!TryRead<long>(br, out long tmpCandID))
                    {
                        Debug.WriteLine($"[WARN] AI[{i}] Candidate DBPulseID 누락");
                        break;
                    }
                    cand.DBPulseID = tmpCandID;

                    byte[] buf31c = br.ReadBytes(31);
                    cand.PulseName = Encoding.Default.GetString(buf31c).TrimEnd('\0', ' ');
                    byte[] buf31d = br.ReadBytes(31);
                    cand.SonarName = Encoding.Default.GetString(buf31d).TrimEnd('\0', ' ');

                    if (!TryRead<double>(br, out double tmpCandScore))
                    {
                        Debug.WriteLine($"[WARN] AI[{i}] Candidate Score 누락");
                        break;
                    }
                    cand.Score = tmpCandScore;

                    if (!TryRead<int>(br, out int tmpRank))
                    {
                        Debug.WriteLine($"[WARN] AI[{i}] Candidate Rank 누락");
                        break;
                    }
                    cand.Rank = tmpRank;

                    if (!TryRead<int>(br, out int tmpSegCount))
                    {
                        Debug.WriteLine($"[WARN] AI[{i}] Candidate SegmentCount 누락");
                        break;
                    }
                    cand.SegmentCount = tmpSegCount;

                    if (!TryRead<bool>(br, out bool tmpIsSel))
                    {
                        Debug.WriteLine($"[WARN] AI[{i}] Candidate IsSelected 누락");
                        break;
                    }
                    cand.IsSelected = tmpIsSel;

                    // 세그먼트 읽기
                    if (!TryRead<int>(br, out int segCount))
                        segCount = 0;

                    for (int j = 0; j < segCount; j++)
                    {
                        var seg = new AI_SEGMENT_RESULT();

                        if (!TryRead<long>(br, out long tmpSegID))
                        {
                            Debug.WriteLine($"[WARN] AI[{i}] Seg[{j}] SegmentID 누락");
                            break;
                        }
                        seg.SegmentID = tmpSegID;

                        if (!TryRead<double>(br, out double tmpSegScore))
                        {
                            Debug.WriteLine($"[WARN] AI[{i}] Seg[{j}] Score 누락");
                            break;
                        }
                        seg.Score = tmpSegScore;

                        if (!TryRead<double>(br, out double tmpStartFreq))
                        {
                            Debug.WriteLine($"[WARN] AI[{i}] Seg[{j}] StartFreq 누락");
                            break;
                        }
                        seg.StartFreq = tmpStartFreq;

                        if (!TryRead<double>(br, out double tmpEndFreq))
                        {
                            Debug.WriteLine($"[WARN] AI[{i}] Seg[{j}] EndFreq 누락");
                            break;
                        }
                        seg.EndFreq = tmpEndFreq;

                        cand.listSegmentResult.Add(seg);
                    }

                    ev.listCandidate.Add(cand);
                }

                result.Add(ev);
            }

            return result;
        }
    }
}