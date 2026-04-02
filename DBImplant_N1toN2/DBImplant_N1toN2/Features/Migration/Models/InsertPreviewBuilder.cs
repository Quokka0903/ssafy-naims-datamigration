using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using DBImplant_N1toN2.Features.FileBrowser;
using DBImplant_N1toN2.Features.ShortPeriod.Models;
using DBImplant_N1toN2.Features.ShortPeriod.Parsers;
using static DBImplant_N1toN2.Features.ShortPeriod.Parsers.SdLoader;

namespace DBImplant_N1toN2.Features.Migration.Models
{
    /// <summary>
    /// 파싱된 실제 데이터 + 폴더 스캔 결과를 기반으로 INSERT될 레코드를 미리 생성한다.
    ///
    /// 매핑 구조:
    ///   수집원음자료/임무폴더           → RAW_COLLECT_SIG
    ///     ├─ 협대역/*.wav              → RAW_SCHANNEL_INFO
    ///     ├─ 협대역/*.grm,.dat,.txt    → RAW_SCHANNEL_NB_INFO (wav 이름 그룹)
    ///     └─ *.sd/                     → 단주기 동일 파싱 (SPS/LST/SET)
    ///   협대역원음자료/                → PREC_SRC_SIG
    ///   단주기원음자료/                → PREC_SRC_SIG
    /// </summary>
    public static class InsertPreviewBuilder
    {
        public static List<InsertPreviewRecord> Build(FileBrowserViewModel fileBrowser)
        {
            var records = new List<InsertPreviewRecord>();

            try
            {
                // ── 1. 수집원음자료 → RAW_COLLECT_SIG + RAW_SCHANNEL_INFO + RAW_SCHANNEL_NB_INFO
                BuildRawCollect(records, fileBrowser);

                // ── 2. 단주기원음자료(SD 파싱) → PREC_SRC_SIG + 이벤트/세그먼트/프레임
                BuildSdParsed(records, fileBrowser);

                // ── 3. 협대역원음자료 → PREC_SRC_SIG + NB 파싱 결과
                BuildNbParsed(records, fileBrowser);

                App.WriteLog($"[InsertPreview] 총 {records.Count}개 레코드 생성 완료");
            }
            catch (Exception ex)
            {
                App.WriteLog($"[InsertPreview] 예외: {ex}");
                records.Add(Rec("오류", "(빌드 실패)", 0,
                    "Message", ex.Message, "", "", "", "", "", "", "", "", "", ""));
            }

            return records;
        }

        // ══════════════════════════════════════════════════════════════
        // 1. 수집원음자료 → RAW_COLLECT_SIG
        // ══════════════════════════════════════════════════════════════
        private static void BuildRawCollect(List<InsertPreviewRecord> recs, FileBrowserViewModel fb)
        {
            var rawRoot = AppSettingsHelper.GetCollectRaw();
            App.WriteLog($"[InsertPreview] Raw root: {rawRoot}, exists={Directory.Exists(rawRoot)}");
            if (!Directory.Exists(rawRoot)) return;

            var missionDirs = SafeGetDirectories(rawRoot);
            int missionIdx = 0;

            foreach (var missionDir in missionDirs)
            {
                missionIdx++;
                var missionName = Path.GetFileName(missionDir);

                // 임무 폴더 = RAW_COLLECT_SIG 1건
                recs.Add(Rec("RAW_COLLECT_SIG", missionName, missionIdx,
                    "MISSION_NAME", missionName,
                    "FOLDER_PATH",  missionDir,
                    "STATUS",       "INSERT",
                    "", "", "", "", "", ""));

                // 협대역 하위 폴더 탐색
                var nbDir = Path.Combine(missionDir, "협대역");
                if (!Directory.Exists(nbDir))
                    nbDir = SafeGetDirectories(missionDir)
                        .FirstOrDefault(d => Path.GetFileName(d).Contains("협대역")) ?? "";

                if (Directory.Exists(nbDir))
                    BuildRawNbChannel(recs, nbDir, missionName);

                // .sd 하위 폴더 탐색 (단주기와 동일 파싱, cla/tmp/snippet 없음)
                var sdDirs = SafeGetDirectories(missionDir)
                    .Where(d => d.EndsWith(".sd", StringComparison.OrdinalIgnoreCase));

                foreach (var sdDir in sdDirs)
                {
                    var sdName = Path.GetFileName(sdDir);
                    BuildRawSdFolder(recs, sdDir, $"{missionName}/{sdName}");
                }
            }
        }

        /// <summary>수집원음 > 협대역 폴더 내 WAV/GRM/DAT/TXT → RAW_SCHANNEL_INFO + RAW_SCHANNEL_NB_INFO</summary>
        private static void BuildRawNbChannel(List<InsertPreviewRecord> recs, string nbDir, string missionName)
        {
            var allFiles = SafeGetFiles(nbDir, "*.*", SearchOption.TopDirectoryOnly);

            // WAV 파일 기준으로 그룹핑
            var wavFiles = allFiles.Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)).ToList();

            int wavIdx = 0;
            foreach (var wav in wavFiles.Take(100))
            {
                wavIdx++;
                var fi = new FileInfo(wav);
                var baseName = Path.GetFileNameWithoutExtension(wav);

                // RAW_SCHANNEL_INFO — WAV 파일 1개당 1레코드
                recs.Add(Rec("RAW_SCHANNEL_INFO", missionName, wavIdx,
                    "WAV_FILE",   fi.Name,
                    "FILE_SIZE",  $"{fi.Length:N0} B",
                    "MODIFIED",   $"{fi.LastWriteTime:yyyy-MM-dd HH:mm}",
                    "BASE_NAME",  baseName,
                    "ACTION",     "INSERT",
                    "", ""));

                // 같은 이름의 .grm, .dat, .txt → RAW_SCHANNEL_NB_INFO
                var associatedFiles = allFiles
                    .Where(f => !f.Equals(wav, StringComparison.OrdinalIgnoreCase) &&
                                Path.GetFileNameWithoutExtension(f)
                                    .Equals(baseName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                int nbInfoIdx = 0;
                foreach (var assocFile in associatedFiles)
                {
                    nbInfoIdx++;
                    var afi = new FileInfo(assocFile);
                    var ext = Path.GetExtension(assocFile).ToUpperInvariant();
                    recs.Add(Rec("RAW_SCHANNEL_NB_INFO", missionName, nbInfoIdx,
                        "FILE_NAME",  afi.Name,
                        "FILE_TYPE",  ext,
                        "FILE_SIZE",  $"{afi.Length:N0} B",
                        "BASE_NAME",  baseName,
                        "WAV_REF",    fi.Name,
                        "ACTION",     "INSERT"));
                }
            }
        }

        /// <summary>수집원음 > .sd 폴더 → 단주기 동일 파싱 (SPS/LST/SET, cla/tmp 없음)</summary>
        private static void BuildRawSdFolder(List<InsertPreviewRecord> recs, string sdDir, string label)
        {
            // SPS 파일
            var spsFiles = SafeGetFiles(sdDir, "SPSlist*.*", SearchOption.TopDirectoryOnly);
            foreach (var f in spsFiles)
                recs.Add(Rec("SD_EVENT(Raw내)", label, 1,
                    "FILE", Path.GetFileName(f), "TYPE", "SPS",
                    "SIZE", $"{new FileInfo(f).Length:N0} B",
                    "ACTION", "파싱→SPS이벤트/세그먼트",
                    "", "", "", ""));

            // LST 파일
            var lstFiles = SafeGetFiles(sdDir, "*.lst", SearchOption.TopDirectoryOnly);
            foreach (var f in lstFiles)
                recs.Add(Rec("SD_EVENT(Raw내)", label, 1,
                    "FILE", Path.GetFileName(f), "TYPE", "LST",
                    "SIZE", $"{new FileInfo(f).Length:N0} B",
                    "ACTION", "파싱→이벤트리스트",
                    "", "", "", ""));

            // SET 파일
            var setFiles = SafeGetFiles(sdDir, "*.set", SearchOption.TopDirectoryOnly);
            foreach (var f in setFiles)
                recs.Add(Rec("SD_PARAM(Raw내)", label, 1,
                    "FILE", Path.GetFileName(f), "TYPE", "SET",
                    "SIZE", $"{new FileInfo(f).Length:N0} B",
                    "ACTION", "파싱→분석파라미터",
                    "", "", "", ""));

            // Signal WAV
            var wavFiles = SafeGetFiles(sdDir, "Signal_*.wav", SearchOption.TopDirectoryOnly);
            int wIdx = 0;
            foreach (var f in wavFiles.Take(30))
            {
                wIdx++;
                recs.Add(Rec("Signal WAV(Raw내)", label, wIdx,
                    "FILE", Path.GetFileName(f),
                    "SIZE", $"{new FileInfo(f).Length:N0} B",
                    "ACTION", "경로참조→EventID",
                    "", "", "", "", "", ""));
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 2. 단주기원음자료 → PREC_SRC_SIG + 파싱된 이벤트/세그먼트/프레임
        // ══════════════════════════════════════════════════════════════
        private static void BuildSdParsed(List<InsertPreviewRecord> recs, FileBrowserViewModel fb)
        {
            var sdTab = fb.SdTab;
            App.WriteLog($"[InsertPreview] SdTab null? {sdTab == null}, Packages={sdTab?.Packages?.Count ?? -1}");
            if (sdTab?.Packages == null) return;

            int pkgIdx = 0;
            foreach (var pkg in sdTab.Packages)
            {
                pkgIdx++;
                var pkgName = pkg.PreciseName;

                // PREC_SRC_SIG — SD 패키지 1개당 1건
                recs.Add(Rec("PREC_SRC_SIG(SD)", pkgName, pkgIdx,
                    "PRECISE_NAME", pkgName,
                    "SD_DIR",       pkg.SdDir,
                    "WAV_COUNT",    $"{pkg.RootWavCount}",
                    "SPS_EVENTS",   $"{pkg.SpsEventCount}",
                    "LST_EVENTS",   $"{pkg.LstEventCount}",
                    "STATUS",       pkg.Status));

                // 파싱 결과가 있으면 이벤트/세그먼트/프레임 추가
                var parsed = pkg.Parsed;
                if (parsed == null) continue;

                AddSetParam(recs, parsed.SetParam, pkgName);
                AddEventsAndSegments(recs, parsed.Sps.Events, pkgName, "SPS");
                AddEventsAndSegments(recs, parsed.EventLst.Events, pkgName, "LST");
                AddEventsAndSegments(recs, parsed.Ai.Lst.Events, pkgName, "AI");
                AddAiCla(recs, parsed.Ai.AiClaPulses, pkgName);
                AddAiTmp(recs, parsed.Ai.AiTmpEvents, pkgName);
                AddEventsAndSegments(recs, parsed.Tr.Lst.Events, pkgName, "TR");
                AddTrCla(recs, parsed.Tr.TrClaSegments, pkgName);
                AddTrTmp(recs, parsed.Tr.TrTmpEvents, pkgName);
                AddSignalWavs(recs, parsed.RootSignalWavs, pkgName);
                AddSnippetFiles(recs, parsed.Ai.SnippetWavs, pkgName, "AI Snippet");
                AddSnippetFiles(recs, parsed.Tr.SnippetWavs, pkgName, "TR Snippet");
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 3. 협대역원음자료 → PREC_SRC_SIG
        // ══════════════════════════════════════════════════════════════
        private static void BuildNbParsed(List<InsertPreviewRecord> recs, FileBrowserViewModel fb)
        {
            var nbRoot = AppSettingsHelper.GetNarrowbandRaw();
            App.WriteLog($"[InsertPreview] NB root: {nbRoot}, exists={Directory.Exists(nbRoot)}");
            if (!Directory.Exists(nbRoot)) return;

            var missionDirs = SafeGetDirectories(nbRoot);
            int idx = 0;
            foreach (var dir in missionDirs.Take(20))
            {
                idx++;
                var name = Path.GetFileName(dir);

                recs.Add(Rec("PREC_SRC_SIG(NB)", name, idx,
                    "MISSION_NAME", name,
                    "FOLDER_PATH",  dir,
                    "STATUS",       "INSERT",
                    "", "", "", "", "", ""));

                // NB 파일 탐색
                var nbFiles = SafeGetFiles(dir, "*.*", SearchOption.AllDirectories);
                var feaFiles = nbFiles.Where(f => f.EndsWith(".fea", StringComparison.OrdinalIgnoreCase));
                var claFiles = nbFiles.Where(f => f.EndsWith(".cla", StringComparison.OrdinalIgnoreCase));
                var datFiles = nbFiles.Where(f => f.EndsWith(".dat", StringComparison.OrdinalIgnoreCase));

                int fIdx = 0;
                foreach (var f in feaFiles.Take(10))
                {
                    fIdx++;
                    recs.Add(Rec("NB_FEATURE(NB)", name, fIdx,
                        "FILE", Path.GetFileName(f), "TYPE", "FEA",
                        "SIZE", $"{new FileInfo(f).Length:N0} B",
                        "ACTION", "파싱→NB특징정보",
                        "", "", "", ""));
                }
                foreach (var f in claFiles.Take(10))
                {
                    fIdx++;
                    recs.Add(Rec("NB_CLASSIFICATION(NB)", name, fIdx,
                        "FILE", Path.GetFileName(f), "TYPE", "CLA",
                        "SIZE", $"{new FileInfo(f).Length:N0} B",
                        "ACTION", "파싱→NB분류결과",
                        "", "", "", ""));
                }
            }
        }

        // ── SD 파싱 데이터 → 레코드 변환 (공통) ──────────────────────

        private static void AddSetParam(List<InsertPreviewRecord> recs, SetParamRow? set, string pkg)
        {
            if (set == null || !set.Exists) return;
            recs.Add(Rec("PREC_SS_PARAM", pkg, 1,
                "DT_THRESHOLD", $"{set.DtThreshold}",
                "BW_THRESHOLD", $"{set.BwThreshold}",
                "DETECT_THR",   $"{set.DetectThreshold}",
                "FILE_PATH",    set.FilePath ?? "",
                "ERROR",        set.Error ?? "",
                "STATUS",       "OK"));
        }

        private static void AddEventsAndSegments(
            List<InsertPreviewRecord> recs,
            ObservableCollection<ShortPeriodEvent>? events,
            string pkg, string source)
        {
            if (events == null || events.Count == 0) return;
            int evIdx = 0;
            foreach (var ev in events.Take(100))
            {
                evIdx++;
                recs.Add(Rec($"PREC_SS_EVENT[{source}]", pkg, evIdx,
                    "EVENT_ID",    $"{ev.EventID}",
                    "START_TIME",  $"{ev.StartTime}",
                    "END_TIME",    $"{ev.EndTime}",
                    "DETECT_TYPE", ev.DetectingType ?? "",
                    "SEG_COUNT",   $"{ev.SegmentCount}",
                    "MODULATION",  ev.Modulation ?? ""));

                if (ev.Segments == null) continue;
                int segIdx = 0;
                foreach (var seg in ev.Segments.Take(50))
                {
                    segIdx++;
                    recs.Add(Rec($"PREC_SS_SEGMENT[{source}]", pkg, segIdx,
                        "SEG_ID",      $"{seg.SegmentID}",
                        "FREQ_START",  $"{seg.StartFreq:F1}",
                        "FREQ_END",    $"{seg.EndFreq:F1}",
                        "CENTER_FREQ", $"{seg.CenterFreq:F1}",
                        "BANDWIDTH",   $"{seg.BandWidth:F1}",
                        "FRAME_COUNT", $"{seg.FrameCount}"));

                    if (seg.Frames == null) continue;
                    int frmIdx = 0;
                    foreach (var fr in seg.Frames.Take(20))
                    {
                        frmIdx++;
                        var mfb = fr.MFBs?.Count > 0
                            ? string.Join(",", fr.MFBs.Take(4).Select(m => $"{m:F2}")) + "..."
                            : "";
                        recs.Add(Rec($"PREC_SS_FRAME[{source}]", pkg, frmIdx,
                            "FRAME_ID",  $"{fr.FrameID}",
                            "FREQUENCY", $"{fr.Frequency:F1}",
                            "SCORE",     $"{fr.Score:F3}",
                            "MFB_COUNT", $"{fr.MFBs?.Count ?? 0}",
                            "MFB_VALS",  mfb,
                            "DB_SEG_ID", $"{fr.DBSegID}"));
                    }
                }
            }
        }

        private static void AddAiCla(List<InsertPreviewRecord> recs,
            ObservableCollection<AiClaPulseRow>? pulses, string pkg)
        {
            if (pulses == null) return;
            int idx = 0;
            foreach (var p in pulses.Take(50))
            {
                idx++;
                recs.Add(Rec("AI_CLA_RESULT", pkg, idx,
                    "PULSE_ID",   $"{p.PID}",
                    "SONAR_NAME", p.SonarName,
                    "PULSE_NAME", p.PulseName,
                    "SCORE",      $"{p.Score:F3}",
                    "SEG_COUNT",  $"{p.SegmentCount}",
                    "COMMENT",    p.Comment));
            }
        }

        private static void AddAiTmp(List<InsertPreviewRecord> recs,
            ObservableCollection<AiTmpEventRow>? events, string pkg)
        {
            if (events == null) return;
            int idx = 0;
            foreach (var e in events.Take(50))
            {
                idx++;
                recs.Add(Rec("AI_TMP_RESULT", pkg, idx,
                    "EVENT_ID",      $"{e.EventID}",
                    "SONAR_NAME",    e.ConfirmedSonarName,
                    "PULSE_NAME",    e.ConfirmedPulseName,
                    "SCORE",         $"{e.ConfirmedScore:F3}",
                    "CANDIDATE_CNT", $"{e.CandidateCount}",
                    "", ""));
            }
        }

        private static void AddTrCla(List<InsertPreviewRecord> recs,
            ObservableCollection<TrClaSegmentRow>? segs, string pkg)
        {
            if (segs == null) return;
            int idx = 0;
            foreach (var s in segs.Take(50))
            {
                idx++;
                recs.Add(Rec("TR_CLA_RESULT", pkg, idx,
                    "EVENT_ID",  $"{s.EventID}",
                    "SEG_ID",    $"{s.SegmentID}",
                    "GROUP",     s.Group,
                    "TR_NAME",   s.TransientName,
                    "FRAME_NUM", $"{s.FrameNum}",
                    "SCORE",     $"{s.Score:F3}"));
            }
        }

        private static void AddTrTmp(List<InsertPreviewRecord> recs,
            ObservableCollection<TrTmpEventRow>? events, string pkg)
        {
            if (events == null) return;
            int idx = 0;
            foreach (var e in events.Take(50))
            {
                idx++;
                recs.Add(Rec("TR_TMP_RESULT", pkg, idx,
                    "EVENT_ID",      $"{e.EventID}",
                    "CONFIRMED_CNT", $"{e.ConfirmedCount}",
                    "SEGMENT_CNT",   $"{e.SegmentCount}",
                    "", "", "", "", "", ""));
            }
        }

        private static void AddSignalWavs(List<InsertPreviewRecord> recs,
            ObservableCollection<FileRow>? wavs, string pkg)
        {
            if (wavs == null) return;
            int idx = 0;
            foreach (var w in wavs.Take(30))
            {
                idx++;
                recs.Add(Rec("Signal WAV→경로참조", pkg, idx,
                    "FILE_NAME", w.RelativePath,
                    "SIZE",      $"{w.Length:N0} B",
                    "MODIFIED",  $"{w.LastWriteTime:yyyy-MM-dd HH:mm}",
                    "ACTION",    "EventID 매핑",
                    "", "", "", ""));
            }
        }

        private static void AddSnippetFiles(List<InsertPreviewRecord> recs,
            ObservableCollection<FileRow>? files, string pkg, string label)
        {
            if (files == null) return;
            int idx = 0;
            foreach (var f in files.Take(20))
            {
                idx++;
                recs.Add(Rec($"{label}→BLOB", pkg, idx,
                    "FILE_NAME", f.RelativePath,
                    "SIZE",      $"{f.Length:N0} B",
                    "MODIFIED",  $"{f.LastWriteTime:yyyy-MM-dd HH:mm}",
                    "ACTION",    "BLOB INSERT",
                    "", "", "", ""));
            }
        }

        // ── 유틸 ─────────────────────────────────────────────────────

        private static InsertPreviewRecord Rec(
            string table, string pkg, int row,
            string c1n, string c1v, string c2n, string c2v,
            string c3n, string c3v, string c4n, string c4v,
            string c5n, string c5v, string c6n, string c6v)
            => new()
            {
                TargetTable = table, SourcePackage = pkg, RowIndex = row,
                Col1Name = c1n, Col1Value = c1v,
                Col2Name = c2n, Col2Value = c2v,
                Col3Name = c3n, Col3Value = c3v,
                Col4Name = c4n, Col4Value = c4v,
                Col5Name = c5n, Col5Value = c5v,
                Col6Name = c6n, Col6Value = c6v,
            };

        private static string[] SafeGetDirectories(string path)
        {
            try { return Directory.GetDirectories(path); }
            catch { return Array.Empty<string>(); }
        }

        private static string[] SafeGetFiles(string path, string pattern, SearchOption opt = SearchOption.TopDirectoryOnly)
        {
            try { return Directory.GetFiles(path, pattern, opt); }
            catch { return Array.Empty<string>(); }
        }
    }
}
