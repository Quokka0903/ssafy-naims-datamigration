using DBImplant_N1toN2.Features.ShortPeriod.Models;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using static DBImplant_N1toN2.Features.ShortPeriod.Parsers.ClaFileReader;
using static DBImplant_N1toN2.Features.ShortPeriod.Parsers.EventListReader;

namespace DBImplant_N1toN2.Features.ShortPeriod.Parsers
{
    public static partial class SdParser
    {
        private static void ParseAnalysisFolder(
            string folderPath,
            string lstName,
            string claName,
            string tmpName,
            bool isTrLst,
            bool isAi,
            AnalysisFolderParseRow dst,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            dst.FolderPath = folderPath;
            dst.Exists = Directory.Exists(folderPath);

            // 비우기
            dst.Lst.Events.Clear();
            dst.SnippetWavs.Clear();
            dst.SnippetGrms.Clear();
            dst.AiClaPulses.Clear();
            dst.TrClaSegments.Clear();
            dst.AiTmpEvents.Clear();
            dst.AiTmpCandidates.Clear();
            dst.AiTmpSegments.Clear();
            dst.TrTmpEvents.Clear();
            dst.TrTmpSegments.Clear();

            dst.Lst.Error = null;
            dst.ClaError = null;
            dst.TmpError = null;

            if (!dst.Exists) return;

            // (1) LST
            ParseLst(folderPath, lstName, isTR: isTrLst, dst.Lst);

            // (2) Snippets 파일 리스트화
            var snippetDir = Path.Combine(folderPath, "Snippets");
            if (Directory.Exists(snippetDir))
            {
                FillFiles(dst.SnippetWavs, snippetDir, "*.wav", SearchOption.TopDirectoryOnly);
                FillFiles(dst.SnippetGrms, snippetDir, "*.grm", SearchOption.TopDirectoryOnly);
            }

            // (3) CLA
            var claPath = Directory.GetFiles(folderPath, claName, SearchOption.TopDirectoryOnly).FirstOrDefault();
            dst.ClaPath = claPath;
            dst.ClaExists = claPath != null && File.Exists(claPath);

            if (dst.ClaExists)
            {
                try
                {
                    if (isAi)
                    {
                        AIClassificationResult cla = ReadCCFAIClassificationResult(claPath!);
                        foreach (var p in cla.listPulseResults)
                        {
                            dst.AiClaPulses.Add(new AiClaPulseRow
                            {
                                PID = p.aiPulseResult.infoPulse.lPID,
                                SonarName = p.aiPulseResult.strSonarName ?? "",
                                PulseName = p.aiPulseResult.strPulseName ?? "",
                                Score = p.aiPulseResult.fScore,
                                SegmentCount = p.listSegmentResults?.Count ?? 0,
                                GramSnippet = p.aiPulseResult.strGramSnippetFileName ?? "",
                                AudioSnippet = p.aiPulseResult.strAudioSnippetFileName ?? "",
                                Comment = p.aiPulseResult.strComment ?? ""
                            });
                        }
                    }
                    else
                    {
                        TRClassificationResult cla = ReadCCFTRClassificationResult(claPath!);
                        foreach (var s in cla.listTrResult)
                        {
                            dst.TrClaSegments.Add(new TrClaSegmentRow
                            {
                                EventID = s.lEventID,
                                SegmentID = s.lSegmentID,
                                Group = s.strGroup ?? "",
                                TransientName = s.strTransientName ?? "",
                                FrameNum = s.nFrameNum,
                                Score = s.fScore,
                                AudioSnippet = s.strAudioSnippetFileName ?? "",
                                AppliedState = s.byteAppliedState,
                                DBApplyID = s.lDBApplyID,
                                SnippetAppliedState = s.byteSnippetAppliedState
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    dst.ClaError = ex.Message;
                }
            }

            // (4) TMP
            var tmpPath = Directory.GetFiles(folderPath, tmpName, SearchOption.TopDirectoryOnly).FirstOrDefault();
            dst.TmpPath = tmpPath;
            dst.TmpExists = tmpPath != null && File.Exists(tmpPath);

            if (dst.TmpExists)
            {
                try
                {
                    if (isAi)
                    {
                        var evs = TmpFileReader.ReadAIResultListFile(tmpPath!);

                        foreach (var ev in evs)
                        {
                            dst.AiTmpEvents.Add(new AiTmpEventRow
                            {
                                EventID = ev.EventID,
                                ConfirmedSonarName = ev.ConfirmedSonarName ?? "",
                                ConfirmedPulseName = ev.ConfirmedPulseName ?? "",
                                ConfirmedScore = ev.ConfirmedScore,
                                CandidateCount = ev.listCandidate?.Count ?? 0
                            });

                            foreach (var c in ev.listCandidate)
                            {
                                dst.AiTmpCandidates.Add(new AiTmpCandidateRow
                                {
                                    EventID = ev.EventID,
                                    Rank = c.Rank,
                                    DBPulseID = c.DBPulseID,
                                    SonarName = c.SonarName ?? "",
                                    PulseName = c.PulseName ?? "",
                                    Score = c.Score,
                                    SegmentCount = c.SegmentCount,
                                    IsSelected = c.IsSelected
                                });

                                foreach (var s in c.listSegmentResult)
                                {
                                    dst.AiTmpSegments.Add(new AiTmpSegmentRow
                                    {
                                        EventID = ev.EventID,
                                        Rank = c.Rank,
                                        SegmentID = s.SegmentID,
                                        DbSegmentID = s.DbSegmentID,
                                        Score = s.Score,
                                        StartFreq = 0,
                                        EndFreq = 0
                                    });
                                }
                            }
                        }
                    }
                    else
                    {
                        // 0) LST의 (EventID, SegmentID) -> (StartTime, EndTime) 룩업 준비
                        //    dst.Lst.Events가 ShortPeriodEvent를 담고 있다는 가정(속성명은 필요시 조정)
                        var segTime = dst.Lst.Events
                            .SelectMany(e => e.Segments.Select(s =>
                                new { e.EventID, s.SegmentID, s.StartTime, s.EndTime }))
                            .ToDictionary(x => (x.EventID, x.SegmentID), x => (x.StartTime, x.EndTime));

                        // 1) TMP 읽기 (C++ 포맷 호환 리더)
                        var evs = TmpFileReader.ReadTRResultListFile(tmpPath!);

                        foreach (var ev in evs)
                        {
                            dst.TrTmpEvents.Add(new TrTmpEventRow
                            {
                                EventID = ev.EventId,
                                ConfirmedCount = ev.ConfirmedCount,
                                SegmentCount = ev.SegmentCount
                            });

                            foreach (var s in ev.Segments)
                            {
                                // 2) TMP의 SegmentID로 LST의 시간값을 찾아서 채움
                                long st = 0, et = 0;
                                if (segTime.TryGetValue((ev.EventId, s.SegmentId), out var t))
                                {
                                    st = t.StartTime;
                                    et = t.EndTime;
                                }

                                // 3) 기존 Row에 맞춰 채우기 (Score는 MatchRate로 대체)
                                dst.TrTmpSegments.Add(new TrTmpSegmentRow
                                {
                                    EventID = ev.EventId,
                                    SegmentID = s.SegmentId,
                                    StartTime = st,
                                    EndTime = et,
                                    Score = s.MatchRate, // (Row에 필드가 있다면 SourceName/Type/Comment도 같이 저장 추천)
                                                         // SourceName = s.SourceName,
                                                         // Type = s.Type,
                                                         // Comment = s.Comment
                                });
                            }
                        }
                    }

                }
                catch (Exception ex)
                {
                    dst.TmpError = ex.Message;
                }
            }
        }

        private static string GetRelativePathSafe(string baseDir, string fullPath)
        {
            try { return Path.GetRelativePath(baseDir, fullPath); }
            catch { return fullPath; }
        }
    }
}
