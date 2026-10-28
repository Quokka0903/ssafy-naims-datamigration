using DBImplant_N1toN2.Features.ShortPeriod.Models;
using DBImplant_N1toN2.Features.ShortPeriod.Parsers;
using DBImplant_N1toN2.Infrastructure;
using DBImplant_N1toN2.Infrastructure.Commands;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static DBImplant_N1toN2.Features.ShortPeriod.Parsers.SdLoader;

namespace DBImplant_N1toN2.Features.ShortPeriod.ViewModels
{
    public sealed partial class ShortPeriodTabViewModel
    {
        private void SelectRootPath()
        {
            var dlg = new OpenFolderDialog
            {
                Title = "단주기원음자료 루트 폴더 선택",
                InitialDirectory = Directory.Exists(RootPath) ? RootPath : @"D:\MSU"
            };
            if (dlg.ShowDialog() == true)
                RootPath = dlg.FolderName;
        }

        private async Task ScanParseAsync()
        {
            if (IsRunning) return;
            IsRunning = true;

            Packages.Clear();
            SelectedPackage = null;

            ClearView();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            try
            {
                if (!Directory.Exists(RootPath))
                    throw new DirectoryNotFoundException(RootPath);

                StatusText = "sd 폴더 스캔 중...";
                Progress = 0;

                var sdDirs = await Task.Run(() =>
                    Directory.EnumerateDirectories(RootPath, "*.sd", SearchOption.AllDirectories)
                             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                             .ToList(), ct);

                int total = sdDirs.Count;
                int done = 0;

                foreach (var sd in sdDirs)
                {
                    ct.ThrowIfCancellationRequested();

                    var item = new SdPackageItem(sd) { Status = "파싱 중..." };
                    Packages.Add(item);
                    SelectedPackage ??= item;

                    try
                    {
                        var parsed = await Task.Run(() => SdParser.Parse(sd, ct), ct);
                        item.Parsed = parsed;

                        item.RootWavCount = parsed.RootSignalWavs.Count;
                        item.SpsEventCount = parsed.Sps.Events.Count;
                        item.LstEventCount = parsed.EventLst.Events.Count;
                        item.HasSet = parsed.SetParam.Exists;
                        item.AiEventCount = parsed.Ai.Lst.Events.Count;
                        item.TrEventCount = parsed.Tr.Lst.Events.Count;

                        item.Status = "완료";
                        item.IsChecked = true; // 기본은 Import 대상 체크
                    }
                    catch (Exception ex)
                    {
                        item.Status = "실패";
                        item.Error = ex.Message;
                    }

                    done++;
                    Progress = total == 0 ? 1 : (double)done / total;
                    if (done % 50 == 0) StatusText = $"진행 {done}/{total}";
                }

                StatusText = $"완료 (총 {total}개)";
                Progress = 1;
            }
            finally
            {
                IsRunning = false;
            }
        }

        private async Task DbImportAsync()
        {
            // 요구사항: "SqlConnection을 통해 DB 삽입" 이지만, 삽입 코드는 TODO로 남겨둠
            // => 연결/트랜잭션 뼈대만 잡고, 실제 Insert는 TODO 처리.

            if (IsRunning) return;
            IsRunning = true;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            try
            {
                var targets = Packages.Where(p => p.IsChecked && p.Parsed != null).ToList();
                if (targets.Count == 0)
                {
                    StatusText = "Import 대상이 없음";
                    return;
                }

                // [NAIMS-ISSUE:NO-EMBEDDED-SECRET] 비밀번호를 코드에 두지 않는다(이 파일은 git 추적 대상).
                //   실행 전 접속문자열을 채워 넣어야 한다.
                string connStr = "Server=tcp:192.168.200.205,1433;Database=NAIMS2;User Id=NAIMS2DB;Password=;Encrypt=true;TrustServerCertificate=true;";

                StatusText = $"DB Import 준비(대상 {targets.Count}개) ...";
                Progress = 0;

                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync(ct);

                // 선택: 전체를 한 트랜잭션으로 묶거나, 패키지별 트랜잭션으로 묶기
                // 여기서는 패키지별로 트랜잭션 분리(중간 실패 시 영향 최소)
                int done = 0;
                foreach (var pkg in targets)
                {
                    ct.ThrowIfCancellationRequested();

                    pkg.Status = "Import(TODO)";
                    pkg.Error = "";

                    await using var tx = conn.BeginTransaction();
                    try
                    {
                        var parsed = pkg.Parsed!;

                        // TODO: 아래 parsed.* 들을 사용해서 Insert 로직 작성
                        // - parsed.EventLst.Events (이벤트리스트.lst)
                        // - parsed.Sps.Events (SPSlist.txt)
                        // - parsed.SetParam (정밀분석설정.set)
                        // - parsed.Ai (AI 폴더 전부)
                        // - parsed.Tr (TR 폴더 전부)
                        //
                        // 예:
                        // await InsertPreciseAnalysisAsync(conn, tx, parsed.PreciseName, ...);
                        // await InsertSsParamAsync(conn, tx, parsed.SetParam.BwThreshold, parsed.SetParam.DtThreshold, ...);
                        // await InsertEventsAsync(conn, tx, parsed.EventLst.Events, ...);
                        // await InsertAiClaAsync(conn, tx, parsed.Ai.AiClaPulses, ...);
                        // ...

                        await tx.CommitAsync(ct);
                        pkg.Status = "Import(TODO 완료 처리)";
                    }
                    catch (Exception ex)
                    {
                        await tx.RollbackAsync(ct);
                        pkg.Status = "Import(TODO 실패)";
                        pkg.Error = ex.Message;
                    }

                    done++;
                    Progress = (double)done / targets.Count;
                }

                StatusText = "DB Import(TODO) 루프 완료";
            }
            finally
            {
                IsRunning = false;
            }
        }

        private void LoadSelectedToView()
        {
            ClearView();

            var p = SelectedPackage?.Parsed;
            if (p == null) return;

            foreach (var f in p.RootSignalWavs) ViewRootSignalWavs.Add(f);

            // SPS
            foreach (var ev in p.Sps.Events) ViewSpsEvents.Add(ev);
            SelectedSpsEvent = ViewSpsEvents.FirstOrDefault();

            // 이벤트리스트.lst
            foreach (var ev in p.EventLst.Events) ViewLstEvents.Add(ev);
            SelectedLstEvent = ViewLstEvents.FirstOrDefault();

            // set
            ViewSetExists = p.SetParam.Exists;
            ViewSetPath = p.SetParam.FilePath ?? "";
            ViewBw = p.SetParam.BwThreshold;
            ViewDt = p.SetParam.DtThreshold;
            ViewSetError = p.SetParam.Error ?? "";
            Raise(nameof(ViewSetExists));
            Raise(nameof(ViewSetPath));
            Raise(nameof(ViewBw));
            Raise(nameof(ViewDt));
            Raise(nameof(ViewSetError));

            // AI
            foreach (var ev in p.Ai.Lst.Events) ViewAiEvents.Add(ev);
            SelectedAiEvent = ViewAiEvents.FirstOrDefault();

            foreach (var f in p.Ai.SnippetWavs) ViewAiSnippetWavs.Add(f);
            foreach (var f in p.Ai.SnippetGrms) ViewAiSnippetGrms.Add(f);
            foreach (var r in p.Ai.AiClaPulses) ViewAiClaPulses.Add(r);
            foreach (var r in p.Ai.AiTmpEvents) ViewAiTmpEvents.Add(r);
            foreach (var r in p.Ai.AiTmpCandidates) ViewAiTmpCandidates.Add(r);
            foreach (var r in p.Ai.AiTmpSegments) ViewAiTmpSegments.Add(r);

            ViewAiErrors = BuildErr(p.Ai);
            Raise(nameof(ViewAiErrors));

            // TR
            foreach (var ev in p.Tr.Lst.Events) ViewTrEvents.Add(ev);
            SelectedTrEvent = ViewTrEvents.FirstOrDefault();

            foreach (var f in p.Tr.SnippetWavs) ViewTrSnippetWavs.Add(f);
            foreach (var f in p.Tr.SnippetGrms) ViewTrSnippetGrms.Add(f);
            foreach (var r in p.Tr.TrClaSegments) ViewTrClaSegments.Add(r);
            foreach (var r in p.Tr.TrTmpEvents) ViewTrTmpEvents.Add(r);
            foreach (var r in p.Tr.TrTmpSegments) ViewTrTmpSegments.Add(r);

            ViewTrErrors = BuildErr(p.Tr);
            Raise(nameof(ViewTrErrors));
        }

        private static void RebuildSegments(ObservableCollection<ShortPeriodSegment> dst, ShortPeriodEvent? ev)
        {
            dst.Clear();
            if (ev?.Segments == null) return;
            foreach (var s in ev.Segments) dst.Add(s);
        }

        private static string BuildErr(AnalysisFolderParseRow a)
        {
            string s = "";
            if (!string.IsNullOrWhiteSpace(a.Lst.Error)) s += $"LST: {a.Lst.Error}  ";
            if (!string.IsNullOrWhiteSpace(a.ClaError)) s += $"CLA: {a.ClaError}  ";
            if (!string.IsNullOrWhiteSpace(a.TmpError)) s += $"TMP: {a.TmpError}  ";
            return s.Trim();
        }

        private void ClearView()
        {
            ViewRootSignalWavs.Clear();

            ViewSpsEvents.Clear();
            ViewSpsSegments.Clear();
            SelectedSpsEvent = null;

            ViewLstEvents.Clear();
            ViewLstSegments.Clear();
            SelectedLstEvent = null;

            ViewAiEvents.Clear();
            ViewAiSegments.Clear();
            SelectedAiEvent = null;
            ViewAiSnippetWavs.Clear();
            ViewAiSnippetGrms.Clear();
            ViewAiClaPulses.Clear();
            ViewAiTmpEvents.Clear();
            ViewAiTmpCandidates.Clear();
            ViewAiTmpSegments.Clear();
            ViewAiErrors = "";
            Raise(nameof(ViewAiErrors));

            ViewTrEvents.Clear();
            ViewTrSegments.Clear();
            ViewTrFrames.Clear();
            SelectedTrEvent = null;
            SelectedTrSegment = null;
            ViewTrSnippetWavs.Clear();
            ViewTrSnippetGrms.Clear();
            ViewTrClaSegments.Clear();
            ViewTrTmpEvents.Clear();
            ViewTrTmpSegments.Clear();
            ViewTrErrors = "";
            Raise(nameof(ViewTrErrors));

            ViewSetExists = false;
            ViewSetPath = "";
            ViewBw = 0;
            ViewDt = 0;
            ViewSetError = "";
            Raise(nameof(ViewSetExists));
            Raise(nameof(ViewSetPath));
            Raise(nameof(ViewBw));
            Raise(nameof(ViewDt));
            Raise(nameof(ViewSetError));
        }
    }
}
