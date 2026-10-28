using DBImplant_N1toN2.Audio.Writer;
using DBImplant_N1toN2.Features.Raw.Models;
using DBImplant_N1toN2.Features.Raw.Parsers;
using DBImplant_N1toN2.Features.ShortPeriod.Parsers;
using SpsReader = DBImplant_N1toN2.Features.Raw.Parsers.SpsListReader;
using DBImplant_N1toN2.Infrastructure;
using DBImplant_N1toN2.Infrastructure.Commands;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using static DBImplant_N1toN2.Models.AppSettings;

namespace DBImplant_N1toN2.Features.Raw.ViewModels
{
    public sealed partial class RawTabViewModel
    {
        private void SelectRootPath()
        {
            var dlg = new OpenFolderDialog
            {
                Title = "수집원음자료 루트 폴더 선택",
                InitialDirectory = Directory.Exists(RootPath) ? RootPath : @"D:\MSU"
            };
            if (dlg.ShowDialog() == true)
                RootPath = dlg.FolderName;
        }

        private async Task ScanParseAllAsync()
        {
            if (IsRunning) return;
            IsRunning = true;

            Packages.Clear();
            SelectedPackage = null;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            try
            {
                if (!Directory.Exists(RootPath))
                    throw new DirectoryNotFoundException(RootPath);

                StatusText = "폴더 구조 스캔 중...";
                Progress = 0;

                // 1) 스캔
                var scan = await Task.Run(() => RawScanner.ScanCollections(RootPath), ct);

                // 2) 임무폴더를 트리 구조로 구성 (CollectionFolder → SD/NB 자식)
                foreach (var col in scan.Collections)
                {
                    int nbWavCount = col.NbFolders.Sum(nb => nb.Groups.Count);
                    var parent = new RawPackageItem(col.DirectoryPath, col.SdPackages.Count, nbWavCount);

                    // SD 패키지를 자식으로
                    foreach (var sd in col.SdPackages)
                        parent.Children.Add(new RawPackageItem(col.DirectoryPath, sd));

                    // NB 그룹을 자식으로
                    foreach (var nb in col.NbFolders)
                    {
                        var nbGroups = new ObservableCollection<NarrowBandGroup>();
                        foreach (var g in nb.Groups)
                            nbGroups.Add(g);
                        parent.Children.Add(new RawPackageItem(col.DirectoryPath, nb.DirectoryPath, nbGroups));
                    }

                    Packages.Add(parent);
                }

                SelectedPackage = Packages.FirstOrDefault();

                // 3) 파일별 파싱(메서드 호출) — 트리 구조 (부모 + 자식)
                // 전체 파싱 대상 수 계산
                int total = Packages.Sum(p => 1 + p.Children.Count);
                int done = 0;

                foreach (var parent in Packages)
                {
                    ct.ThrowIfCancellationRequested();

                    // 부모(CollectionFolder) 폴더 요약(재귀 파일 카운트 등 IO)도 백그라운드에서 채움
                    parent.Status = "파싱 중...";
                    parent.Error = "";
                    parent.Files.Clear();

                    try
                    {
                        await ParseOnePackageAsync(parent, ct);
                        parent.Status = "완료";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        parent.Status = "실패";
                        parent.Error = ex.Message;
                    }

                    done++;
                    Progress = total == 0 ? 1 : (double)done / total;

                    // 자식(SD/NB) 파싱
                    foreach (var child in parent.Children)
                    {
                        ct.ThrowIfCancellationRequested();
                        child.Status = "파싱 중...";
                        child.Error = "";
                        child.Files.Clear();

                        try
                        {
                            await ParseOnePackageAsync(child, ct);
                            child.Status = "완료";
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            child.Status = "실패";
                            child.Error = ex.Message;
                        }

                        done++;
                        Progress = total == 0 ? 1 : (double)done / total;
                        if (done % 20 == 0) StatusText = $"진행 {done}/{total}";
                    }
                }

                StatusText = $"완료 (총 {total}개)";
                Progress = 1;
            }
            catch (OperationCanceledException)
            {
                StatusText = "취소됨";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // DirectoryNotFoundException 등 IO 예외는 팝업 대신 상태 텍스트로 표시
                StatusText = $"실패: {ex.Message}";
            }
            finally
            {
                IsRunning = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private static async Task ParseOnePackageAsync(RawPackageItem pkg, CancellationToken ct)
        {
            // 파싱(GRM/BWF/SPS 해석 + FileInfo IO)은 백그라운드 스레드에서 수행하고,
            // UI 바인딩 컬렉션(Files) 반영만 UI 스레드(await 재개 지점)에서 수행한다.
            var rows = await Task.Run(() => ParsePackageRows(pkg, ct), ct);

            foreach (var row in rows)
                pkg.Files.Add(row);
        }

        private static List<FileParseRow> ParsePackageRows(RawPackageItem pkg, CancellationToken ct)
        {
            var rows = new List<FileParseRow>();

            switch (pkg.Kind)
            {
                case RawPackageKind.CollectionFolder:
                    // 임무 폴더 요약(.sd 재귀 파일 카운트 포함) — 생성자에서 분리한 IO
                    pkg.BuildFolderSummaryRows(rows);
                    break;

                case RawPackageKind.SdPackage:
                    ParseSdPackage(rows, pkg.Sd!, ct);
                    break;

                default:
                    if (pkg.NbGroups != null)
                        ParseNbGroup(rows, pkg.NbGroups, ct);
                    break;
            }

            return rows;
        }

        private static void ParseSdPackage(List<FileParseRow> rows, ShortPeriodSdPackage sd, CancellationToken ct)
        {
            // Sd: wav 여러 개 + SPSlist.txt(옵션)
            foreach (var wav in sd.WavFiles)
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(ParseWavFile(wav));
            }

            if (sd.SpsListPath != null)
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(ParseTextOrBinary(sd.SpsListPath, kind: "SPSlist.txt"));

                // SPSlist 실제 파싱 호출
                var fi = new FileInfo(sd.SpsListPath);
                var events = SpsReader.ReadAll(sd.SpsListPath);
                foreach (var ev in events)
                {
                    // Event와 Segment 정보 처리
                    rows.Add(ParseSpsEvent(ev, fi.FullName, fi.LastWriteTime));
                }
            }
        }

        private static void ParseNbGroup(List<FileParseRow> rows, ObservableCollection<NarrowBandGroup> groups, CancellationToken ct)
        {
            foreach (var g in groups)
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(ParseWavFile(g.WavPath));

                // GRM 파일 — 레거시 .grm 바이너리 파싱 (헤더DB필드 + 전시데이터 변환)
                ParseGrmIfExists(rows, g.DemonGrmPath, "Demon.grm");
                ParseGrmIfExists(rows, g.EventGrmPath, "Event.grm");
                ParseGrmIfExists(rows, g.LofarGrmPath, "Lofar.grm");

                // Drawing 리스트 (CDrawObject 바이너리)
                AddIfExists(rows, g.DemonDrawingListPath, "DemonDrawinglist.txt");
                AddIfExists(rows, g.LofarDrawingListPath, "LofarDrawinglist.txt");

                // _Detect.dat — float[] 탐지 결과
                if (!string.IsNullOrWhiteSpace(g.DetectDatPath))
                {
                    try
                    {
                        var floats = DetectDatParser.ReadDetectDat(g.DetectDatPath!);
                        var fi = new FileInfo(g.DetectDatPath!);
                        rows.Add(new FileParseRow(
                            "Detect.dat", fi.Name, fi.FullName, fi.Length, fi.LastWriteTime,
                            $"float[{floats.Length}], min={floats.DefaultIfEmpty().Min():F2}, max={floats.DefaultIfEmpty().Max():F2}",
                            null));
                    }
                    catch (Exception ex) { AddWithError(rows, g.DetectDatPath!, "Detect.dat", ex.Message); }
                }

                // _NBlist.txt — 발췌 구간 목록
                if (!string.IsNullOrWhiteSpace(g.NbListPath))
                {
                    try
                    {
                        var nbRows = NbListParser.ReadNbList(g.NbListPath!);
                        var fi = new FileInfo(g.NbListPath!);
                        var typeGroups = nbRows.GroupBy(r => r.TypeText).Select(gr => $"{gr.Key}:{gr.Count()}");
                        rows.Add(new FileParseRow(
                            "NBlist.txt", fi.Name, fi.FullName, fi.Length, fi.LastWriteTime,
                            $"{nbRows.Count}개 발췌구간 [{string.Join(", ", typeGroups)}]",
                            null));
                        // 각 발췌 구간을 개별 행으로
                        foreach (var r in nbRows)
                        {
                            rows.Add(new FileParseRow(
                                $"  NB발췌[{r.TypeText}]", $"Frame {r.StartFrame}~{r.EndFrame}", g.NbListPath!,
                                r.ExtractSizeBytes, fi.LastWriteTime,
                                $"type={r.TypeCode}, size={r.ExtractSizeBytes}B, frames={r.StartFrame}~{r.EndFrame}",
                                null));
                        }
                    }
                    catch (Exception ex) { AddWithError(rows, g.NbListPath!, "NBlist.txt", ex.Message); }
                }

                // _SPSlist.txt — SPS 이벤트/세그먼트 (협대역 폴더 내 단주기 이벤트)
                if (!string.IsNullOrWhiteSpace(g.SpsListPath))
                {
                    try
                    {
                        var events = SpsReader.ReadAll(g.SpsListPath!);
                        var fi = new FileInfo(g.SpsListPath!);
                        int totalSegs = events.Sum(e => e.Segs.Count);
                        rows.Add(new FileParseRow(
                            "SPSlist.txt", fi.Name, fi.FullName, fi.Length, fi.LastWriteTime,
                            $"{events.Count}개 이벤트, {totalSegs}개 세그먼트",
                            null));
                        foreach (var ev in events)
                            rows.Add(ParseSpsEvent(ev, fi.FullName, fi.LastWriteTime));
                    }
                    catch (Exception ex) { AddWithError(rows, g.SpsListPath!, "SPSlist.txt", ex.Message); }
                }
            }

            static void AddIfExists(List<FileParseRow> rows, string? path, string kind)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                rows.Add(ParseTextOrBinary(path!, kind));
            }

            static void AddWithError(List<FileParseRow> rows, string path, string kind, string error)
            {
                var fi = new FileInfo(path);
                rows.Add(new FileParseRow(kind, fi.Name, fi.FullName, fi.Length, fi.LastWriteTime, "", error));
            }
        }

        private static void ParseGrmIfExists(List<FileParseRow> rows, string? path, string kind)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var fi = new FileInfo(path!);
            try
            {
                var grm = GrmFileReader.Read(path!);
                if (grm.Error != null)
                {
                    rows.Add(new FileParseRow(kind, fi.Name, fi.FullName, fi.Length, fi.LastWriteTime, "", grm.Error));
                    return;
                }

                string summary;
                if (grm.FileType == LegacyGrmParseResult.GrmFileType.NbSnippet && grm.NbHeader != null)
                {
                    var h = grm.NbHeader;
                    var d = grm.Data;
                    summary = $"NB [{h.GramType}] {h.FrameNum}fr×{h.BinWidth}bin, " +
                              $"freq={h.StartFreq:F1}~{h.EndFreq:F1}Hz, res={h.Resolution:F2}, " +
                              $"det={h.DetYear}-{h.DetMonth:D2}-{h.DetDay:D2}";
                    if (d != null)
                        summary += $", float[{d.FloatBuffer.Length}]";
                }
                else if (grm.FileType == LegacyGrmParseResult.GrmFileType.AiSnippet && grm.AiHeader != null)
                {
                    var h = grm.AiHeader;
                    summary = $"AI {h.FrameNum}fr×{h.BinWidth}bin, " +
                              $"freq={h.StartFreq:F1}~{h.EndFreq:F1}Hz, res={h.Resolution:F2}, " +
                              $"evtID={h.EventID}, sonar={h.DetSonar}";
                }
                else if (grm.FileType == LegacyGrmParseResult.GrmFileType.RawHeaderless && grm.RawData != null)
                {
                    var r = grm.RawData;
                    summary = $"Raw[{r.GramSubType}] {r.FrameCount}fr×{r.BinCount}bin, " +
                              $"short[]→float[{r.FloatBuffer.Length}]";
                }
                else
                {
                    summary = $"[{grm.FileType}]";
                }

                rows.Add(new FileParseRow(kind, fi.Name, fi.FullName, fi.Length, fi.LastWriteTime, summary, null));
            }
            catch (Exception ex)
            {
                rows.Add(new FileParseRow(kind, fi.Name, fi.FullName, fi.Length, fi.LastWriteTime, "", ex.Message));
            }
        }

        private static FileParseRow ParseTextOrBinary(string path, string kind)
        {
            var fi = new FileInfo(path);
            return new FileParseRow(
                Kind: kind,
                FileName: fi.Name,
                FullPath: fi.FullName,
                Size: fi.Length,
                LastWriteTime: fi.LastWriteTime,
                Summary: "",
                Error: null);
        }

        /// <summary>
        /// SPSlist 안의 이벤트 1건을 목록 행으로 만든다.
        /// 경로·수정시각은 이벤트를 담고 있는 SPSlist 파일의 실제 값을 넘겨받는다
        /// (예전에는 "N/A"·DateTime.Now 라는 가짜 메타데이터를 넣어 실제 파일 정보로 오인됐다).
        /// </summary>
        private static FileParseRow ParseSpsEvent(
            (QuickShortPeriodEvent Ev, List<QuickShortPeriodSegment> Segs) eventData,
            string sourcePath, DateTime sourceLastWrite)
        {
            return new FileParseRow(
                Kind: "SPSlist Event",
                FileName: $"Event_{eventData.Ev.EventId}",
                FullPath: sourcePath,
                Size: eventData.Segs.Count * QuickShortPeriodSegment.ByteSize, // Segments 크기
                LastWriteTime: sourceLastWrite,
                Summary: $"Event {eventData.Ev.EventId} - {eventData.Segs.Count} segments",
                Error: null
            );
        }

        private static FileParseRow ParseWavFile(string wavPath)
        {
            var fi = new FileInfo(wavPath);

            try
            {
                var hdr = BwfFile.Read(wavPath);
                if (hdr == null)
                {
                    return new FileParseRow(
                        Kind: "wav",
                        FileName: fi.Name,
                        FullPath: fi.FullName,
                        Size: fi.Length,
                        LastWriteTime: fi.LastWriteTime,
                        Summary: "",
                        Error: "BwfFile.Read returned null");
                }

                var ch = hdr.Format.Channels;
                var sr = hdr.Format.U32SamplesPerSec;
                var bits = hdr.Format.BitsPerSample;

                uint avg = hdr.Format.U32AvgBytesPerSec;
                if (avg == 0 && sr != 0 && hdr.Format.BlockAlign != 0)
                    avg = (uint)(sr * hdr.Format.BlockAlign);

                double durSec = (avg == 0) ? 0 : (hdr.U32DataSize / (double)avg);

                string t0 = "";
                if (TryGetStartTime(hdr, fi, out var dt))
                    t0 = $", t0={dt:yyyy-MM-dd HH:mm:ss}";

                var factId = string.IsNullOrWhiteSpace(hdr.FactID) ? "" : $", fact={hdr.FactID}";

                var summary = $"ch={ch}, sr={sr}, bits={bits}, dur={durSec:0.###}s{t0}{factId}";

                return new FileParseRow(
                    Kind: "wav",
                    FileName: fi.Name,
                    FullPath: fi.FullName,
                    Size: fi.Length,
                    LastWriteTime: fi.LastWriteTime,
                    Summary: summary,
                    Error: null);
            }
            catch (Exception ex)
            {
                return new FileParseRow(
                    Kind: "wav",
                    FileName: fi.Name,
                    FullPath: fi.FullName,
                    Size: fi.Length,
                    LastWriteTime: fi.LastWriteTime,
                    Summary: "",
                    Error: ex.Message);
            }
        }

        private static bool TryGetStartTime(BwfHeader hdr, FileInfo fi, out DateTime dt)
        {
            dt = default;

            var date = hdr.Bext?.OriginationDate?.Trim();
            var time = hdr.Bext?.OriginationTime?.Trim();

            if (!string.IsNullOrWhiteSpace(date) && !string.IsNullOrWhiteSpace(time))
            {
                if (DateTime.TryParseExact(
                    $"{date} {time}",
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out dt))
                    return true;
            }

            int unix = hdr.FactChunk?.StartTime ?? 0;
            if (unix > 0)
            {
                dt = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
                return true;
            }

            return false;
        }

        private async Task DbImportAsync()
        {
            await Task.CompletedTask;
        }
    }

}
