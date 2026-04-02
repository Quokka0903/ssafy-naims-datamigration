using DBImplant_N1toN2.Features.NarrowBand.Models;
using DBImplant_N1toN2.Features.NarrowBand.Parsers;
using DBImplant_N1toN2.Features.Supplement;
using DBImplant_N1toN2.Infrastructure;
using DBImplant_N1toN2.Infrastructure.Commands;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static DBImplant_N1toN2.Features.NarrowBand.Models.NbAuxReaders;
using static DBImplant_N1toN2.Features.NarrowBand.Parsers.NbResultReader;
using static DBImplant_N1toN2.Features.Supplement.AlgReader;

namespace DBImplant_N1toN2.Features.NarrowBand.ViewModels
{
    public sealed partial class NarrowBandTabViewModel
    {
        private void SelectRootPath()
        {
            var dlg = new OpenFolderDialog
            {
                Title = "협대역원음자료 루트 폴더 선택",
                InitialDirectory = Directory.Exists(RootPath) ? RootPath : @"D:\MSU"
            };
            if (dlg.ShowDialog() == true)
            {
                RootPath = dlg.FolderName;
                ScanNbFolders();           // 경로 변경 시 목록 재스캔
            }
        }

        private void ScanNbFolders()
        {
            Packages.Clear();

            if (!Directory.Exists(RootPath))
            {
                StatusText = $"RootPath 없음: {RootPath}";
                Progress = 0;
                return;
            }

            // 요구사항: "CAPTURE"로 시작하지 않고 ".nb"로 끝나는 폴더들
            // - TopDirectoryOnly로 했음 (필요하면 AllDirectories로 바꾸면 됨)
            var nbDirs = Directory.EnumerateDirectories(RootPath, "*.nb", SearchOption.TopDirectoryOnly)
                                  .Where(d =>
                                  {
                                      var name = Path.GetFileName(d);
                                      return !name.StartsWith("CAPTURE", StringComparison.OrdinalIgnoreCase)
                                             && name.EndsWith(".nb", StringComparison.OrdinalIgnoreCase);
                                  })
                                  .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                                  .ToList();

            foreach (var dir in nbDirs)
                Packages.Add(new NbPackageViewModel(dir));

            SelectedPackage = Packages.FirstOrDefault();

            StatusText = $".nb 목록 스캔 완료: {Packages.Count}개";
            Progress = 0;

            // Packages.Count가 변경됐으므로 CanExecute 갱신
            ScanParseCommand?.RaiseCanExecuteChanged();
            DbImportCommand?.RaiseCanExecuteChanged();
        }

        private async Task ScanParseAllAsync()
        {
            if (IsRunning) return;

            IsRunning = true;
            Progress = 0;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            try
            {
                StatusText = $"Load+Parse 시작 (총 {Packages.Count}개)";

                for (int i = 0; i < Packages.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var pkg = Packages[i];
                    pkg.Status = "로딩/파싱 중...";
                    StatusText = $"[{i + 1}/{Packages.Count}] {pkg.PackageName}";

                    await pkg.LoadAndParseAsync(ct);

                    pkg.Status = pkg.Error == null ? "OK" : "WARN";
                    Progress = (i + 1) / (double)Packages.Count;
                }

                StatusText = "Load+Parse 완료";
            }
            catch (OperationCanceledException)
            {
                StatusText = "취소됨";
            }
            catch (Exception ex)
            {
                StatusText = $"실패: {ex.Message}";
            }
            finally
            {
                IsRunning = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void Cancel()
        {
            _cts?.Cancel();
        }
    }

    public sealed partial class NbPackageViewModel
    {
        public async Task LoadAndParseAsync(CancellationToken ct)
        {
            // 백그라운드에서 IO/파싱 수행 → UI 스레드에서 Apply
            var data = await Task.Run(() => NbPackageData.Load(NbDir, ct), ct);
            Apply(data);
        }

        private void Apply(NbPackageData data)
        {
            Error = data.Error;

            AlgPath = data.AlgPath;
            ParsedAlgError = data.AlgError;
            ParsedAlg = data.ParsedAlg;

            // 2컬럼: 루트 파일
            RootItems.Clear();
            foreach (var x in data.RootItems) RootItems.Add(x);

            SetPath = data.SetPath;
            SetError = data.SetError;
            ParsedSet = data.ParsedSet;

            SignalWavPath = data.SignalWavPath;
            RootGrmCount = data.RootGrmCount;
            RootWavCount = data.RootWavCount;
            HasPreciseDir = data.HasPreciseDir;

            // 3컬럼: 정밀분석 폴더 top
            PreciseItems.Clear();
            foreach (var x in data.PreciseItems) PreciseItems.Add(x);

            // 4컬럼: 파싱 결과
            FeaPath = data.FeaPath;
            DatPath = data.DatPath;
            ClaPath = data.ClaPath;

            ParsedFea = data.ParsedFea;
            ParsedDat = data.ParsedDat;
            ParsedCla = data.ParsedCla;

            // 5컬럼: snippets
            Snippets.Clear();
            foreach (var s in data.Snippets) Snippets.Add(s);

            TmpSnippets.Clear();
            foreach (var s in data.TmpSnippets) TmpSnippets.Add(s);

            // 파생 속성도 갱신
            OnPropertyChanged(nameof(PackageName));
            OnPropertyChanged(nameof(SignalWavPath));
            OnPropertyChanged(nameof(RootGrmCount));
            OnPropertyChanged(nameof(RootWavCount));
            OnPropertyChanged(nameof(HasPreciseDir));
            OnPropertyChanged(nameof(FeaPath));
            OnPropertyChanged(nameof(DatPath));
            OnPropertyChanged(nameof(ClaPath));
            OnPropertyChanged(nameof(AlgPath));
            OnPropertyChanged(nameof(ParsedAlgError));
            OnPropertyChanged(nameof(ParsedAlg));
        }
    }
}
