using DBImplant_N1toN2.Audio.Writer;
using DBImplant_N1toN2.Features.Raw.Models;
using DBImplant_N1toN2.Features.Raw.Parsers;
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

                // 2) 패키지(작업) 리스트로 평탄화
                var items = scan.Collections
                    .SelectMany(col =>
                    {
                        var list = new System.Collections.Generic.List<RawPackageItem>();

                        // SdPackage 처리
                        foreach (var sd in col.SdPackages)
                            list.Add(new RawPackageItem(col.DirectoryPath, sd));

                        // NarrowBandGroup 처리
                        foreach (var nb in col.NbFolders)
                        {
                            var nbGroups = new ObservableCollection<NarrowBandGroup>();
                            foreach (var g in nb.Groups)
                            {
                                nbGroups.Add(g);
                            }
                            list.Add(new RawPackageItem(col.DirectoryPath, nb.DirectoryPath, nbGroups));
                        }

                        return list;
                    })
                    .ToList();

                foreach (var it in items)
                    Packages.Add(it);

                SelectedPackage = Packages.FirstOrDefault();

                // 3) 파일별 파싱(메서드 호출)
                int total = Packages.Count;
                int done = 0;

                foreach (var pkg in Packages)
                {
                    ct.ThrowIfCancellationRequested();

                    pkg.Status = "파싱 중...";
                    pkg.Error = "";
                    pkg.Files.Clear();

                    try
                    {
                        await ParseOnePackageAsync(pkg, ct);
                        pkg.Status = "완료";
                    }
                    catch (Exception ex)
                    {
                        pkg.Status = "실패";
                        pkg.Error = ex.Message;
                    }

                    done++;
                    Progress = total == 0 ? 1 : (double)done / total;
                    if (done % 20 == 0) StatusText = $"진행 {done}/{total}";
                }

                StatusText = $"완료 (총 {total}개)";
                Progress = 1;
            }
            finally
            {
                IsRunning = false;
            }
        }

        private static async Task ParseOnePackageAsync(RawPackageItem pkg, CancellationToken ct)
        {
            // "각 파일별 파싱 메서드를 호출"하는 엔트리
            if (pkg.Kind == RawPackageKind.SdPackage)
            {
                await ParseSdPackageAsync(pkg, pkg.Sd!, ct);
                return;
            }

            await ParseNbGroupAsync(pkg, pkg.NbGroups!, ct);
        }

        private static Task ParseSdPackageAsync(RawPackageItem item, ShortPeriodSdPackage sd, CancellationToken ct)
        {
            // Sd: wav 여러 개 + SPSlist.txt(옵션)
            foreach (var wav in sd.WavFiles)
            {
                ct.ThrowIfCancellationRequested();
                item.Files.Add(ParseWavFile(wav));
            }

            if (sd.SpsListPath != null)
            {
                ct.ThrowIfCancellationRequested();
                item.Files.Add(ParseTextOrBinary(sd.SpsListPath, kind: "SPSlist.txt"));
            }

            // SPSlist 실제 파싱 호출
            if (sd.SpsListPath != null)
            {
                var events = SpsListReader.ReadAll(sd.SpsListPath);
                foreach (var ev in events)
                {
                    // Event와 Segment 정보 처리
                    item.Files.Add(ParseSpsEvent(ev));
                }
            }

            return Task.CompletedTask;
        }

        private static Task ParseNbGroupAsync(RawPackageItem item, ObservableCollection<NarrowBandGroup> groups, CancellationToken ct)
        {
            foreach (var g in groups)
            {
                ct.ThrowIfCancellationRequested();
                item.Files.Add(ParseWavFile(g.WavPath));

                // GRM 파일 (바이너리 그램 데이터)
                AddIfExists(item, g.DemonGrmPath, "Demon.grm");
                AddIfExists(item, g.EventGrmPath, "Event.grm");
                AddIfExists(item, g.LofarGrmPath, "Lofar.grm");

                // Drawing 리스트 (CDrawObject 바이너리)
                AddIfExists(item, g.DemonDrawingListPath, "DemonDrawinglist.txt");
                AddIfExists(item, g.LofarDrawingListPath, "LofarDrawinglist.txt");

                // _Detect.dat — float[] 탐지 결과
                if (!string.IsNullOrWhiteSpace(g.DetectDatPath))
                {
                    try
                    {
                        var floats = DetectDatParser.ReadDetectDat(g.DetectDatPath!);
                        var fi = new FileInfo(g.DetectDatPath!);
                        item.Files.Add(new FileParseRow(
                            "Detect.dat", fi.Name, fi.FullName, fi.Length, fi.LastWriteTime,
                            $"float[{floats.Length}], min={floats.DefaultIfEmpty().Min():F2}, max={floats.DefaultIfEmpty().Max():F2}",
                            null));
                    }
                    catch (Exception ex) { AddWithError(item, g.DetectDatPath!, "Detect.dat", ex.Message); }
                }

                // _NBlist.txt — 발췌 구간 목록
                if (!string.IsNullOrWhiteSpace(g.NbListPath))
                {
                    try
                    {
                        var rows = NbListParser.ReadNbList(g.NbListPath!);
                        var fi = new FileInfo(g.NbListPath!);
                        var typeGroups = rows.GroupBy(r => r.TypeText).Select(gr => $"{gr.Key}:{gr.Count()}");
                        item.Files.Add(new FileParseRow(
                            "NBlist.txt", fi.Name, fi.FullName, fi.Length, fi.LastWriteTime,
                            $"{rows.Count}개 발췌구간 [{string.Join(", ", typeGroups)}]",
                            null));
                        // 각 발췌 구간을 개별 행으로
                        foreach (var r in rows)
                        {
                            item.Files.Add(new FileParseRow(
                                $"  NB발췌[{r.TypeText}]", $"Frame {r.StartFrame}~{r.EndFrame}", g.NbListPath!,
                                r.ExtractSizeBytes, fi.LastWriteTime,
                                $"type={r.TypeCode}, size={r.ExtractSizeBytes}B, frames={r.StartFrame}~{r.EndFrame}",
                                null));
                        }
                    }
                    catch (Exception ex) { AddWithError(item, g.NbListPath!, "NBlist.txt", ex.Message); }
                }

                // _SPSlist.txt — SPS 이벤트/세그먼트 (협대역 폴더 내 단주기 이벤트)
                if (!string.IsNullOrWhiteSpace(g.SpsListPath))
                {
                    try
                    {
                        var events = SpsListReader.ReadAll(g.SpsListPath!);
                        var fi = new FileInfo(g.SpsListPath!);
                        int totalSegs = events.Sum(e => e.Segs.Count);
                        item.Files.Add(new FileParseRow(
                            "SPSlist.txt", fi.Name, fi.FullName, fi.Length, fi.LastWriteTime,
                            $"{events.Count}개 이벤트, {totalSegs}개 세그먼트",
                            null));
                        foreach (var ev in events)
                        {
                            item.Files.Add(ParseSpsEvent(ev));
                        }
                    }
                    catch (Exception ex) { AddWithError(item, g.SpsListPath!, "SPSlist.txt", ex.Message); }
                }
            }

            return Task.CompletedTask;

            static void AddIfExists(RawPackageItem item, string? path, string kind)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                item.Files.Add(ParseTextOrBinary(path!, kind));
            }

            static void AddWithError(RawPackageItem item, string path, string kind, string error)
            {
                var fi = new FileInfo(path);
                item.Files.Add(new FileParseRow(kind, fi.Name, fi.FullName, fi.Length, fi.LastWriteTime, "", error));
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

        private static FileParseRow ParseSpsEvent((QuickShortPeriodEvent Ev, List<QuickShortPeriodSegment> Segs) eventData)
        {
            return new FileParseRow(
                Kind: "SPSlist Event",
                FileName: $"Event_{eventData.Ev.EventId}",
                FullPath: "N/A", // Path가 없다면 N/A로
                Size: eventData.Segs.Count * QuickShortPeriodSegment.ByteSize, // Segments 크기
                LastWriteTime: DateTime.Now,
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
