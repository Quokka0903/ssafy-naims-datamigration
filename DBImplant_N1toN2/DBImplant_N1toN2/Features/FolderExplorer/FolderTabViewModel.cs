using DBImplant_N1toN2.Infrastructure;
using DBImplant_N1toN2.Infrastructure.Commands;
using DBImplant_N1toN2.Infrastructure.Utilities;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DBImplant_N1toN2.Features.FolderExplorer
{
    public sealed class FolderTabViewModel : TabViewModel, INotifyPropertyChanged
    {
        private string _rootPath;
        public string RootPath
        {
            get => _rootPath;
            private set => Set(ref _rootPath, value);
        }

        public RelayCommand SelectRootPathCommand { get; }

        public ObservableCollection<FolderNode> Roots { get; } = new();
        public ObservableCollection<DirItem> SelectedSubDirs { get; } = new();
        public ObservableCollection<FileItem> SelectedFiles { get; } = new();
        public ObservableCollection<IndexRow> IndexRows { get; } = new();

        private string _selectedPath = "";
        public string SelectedPath
        {
            get => _selectedPath;
            set => Set(ref _selectedPath, value);
        }

        private string _statusText = "대기 중";
        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set => Set(ref _progress, value);
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (!Set(ref _isRunning, value)) return;
                LoadRootCommand.RaiseCanExecuteChanged();
                BuildIndexCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                SelectRootPathCommand.RaiseCanExecuteChanged();
            }
        }

        public RelayCommand LoadRootCommand { get; }
        public AsyncRelayCommand BuildIndexCommand { get; }
        public RelayCommand CancelCommand { get; }

        /// <summary>TreeView Behavior에서 호출하는 노드 선택 커맨드</summary>
        public RelayCommand<object> OnNodeSelectedCommand { get; }

        private CancellationTokenSource? _cts;

        public FolderTabViewModel(string header, string rootPath) : base(header)
        {
            _rootPath = rootPath;

            LoadRootCommand = new RelayCommand(LoadRoot, () => !IsRunning);
            BuildIndexCommand = new AsyncRelayCommand(BuildIndexAsync, () => !IsRunning);
            CancelCommand = new RelayCommand(Cancel, () => IsRunning);
            SelectRootPathCommand = new RelayCommand(SelectRootPath, () => !IsRunning);
            OnNodeSelectedCommand = new RelayCommand<object>(o =>
            {
                if (o is FolderNode node) OnSelectedNodeChanged(node);
            });

            LoadRoot();
        }

        private void SelectRootPath()
        {
            var dlg = new OpenFolderDialog
            {
                Title = "식별DB 스니펫 루트 폴더 선택",
                InitialDirectory = Directory.Exists(RootPath) ? RootPath : @"D:\MSU"
            };
            if (dlg.ShowDialog() == true)
            {
                RootPath = dlg.FolderName;
                LoadRoot();          // 경로 변경 시 트리 재로드
            }
        }

        private void Cancel() => _cts?.Cancel();

        private void LoadRoot()
        {
            Roots.Clear();
            SelectedSubDirs.Clear();
            SelectedFiles.Clear();
            IndexRows.Clear();

            var rootNode = new FolderNode(RootPath, isRoot: true, loadChildrenAsync: LoadChildrenAsync);
            Roots.Add(rootNode);

            SelectedPath = RootPath;
            StatusText = "루트 로드됨(트리는 펼칠 때 하위 폴더를 읽음)";
            Progress = 0;
        }

        public void OnSelectedNodeChanged(FolderNode node)
        {
            SelectedPath = node.FullPath;
            RefreshSelectedDetails(node.FullPath);
        }

        private void RefreshSelectedDetails(string path)
        {
            SelectedSubDirs.Clear();
            SelectedFiles.Clear();

            try
            {
                foreach (var d in Directory.EnumerateDirectories(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    SelectedSubDirs.Add(new DirItem(Path.GetFileName(d), d));

                foreach (var f in Directory.EnumerateFiles(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var fi = new FileInfo(f);
                    SelectedFiles.Add(new FileItem(fi.Name, fi.FullName, fi.Length, fi.LastWriteTime));
                }

                StatusText = $"선택 폴더 읽음: {SelectedSubDirs.Count} dirs, {SelectedFiles.Count} files";
            }
            catch (Exception ex)
            {
                StatusText = $"폴더 접근 실패: {ex.Message}";
            }
        }

        private async Task LoadChildrenAsync(FolderNode node, CancellationToken ct)
        {
            // TreeView 확장 시 하위 폴더 로드
            var path = node.FullPath;
            string[] dirs;

            try
            {
                dirs = await Task.Run(() =>
                    Directory.EnumerateDirectories(path).ToArray(), ct);
            }
            catch
            {
                dirs = Array.Empty<string>();
            }

            node.Children.Clear();
            foreach (var d in dirs.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                node.Children.Add(new FolderNode(d, isRoot: false, loadChildrenAsync: LoadChildrenAsync));
        }

        private async Task BuildIndexAsync()
        {
            if (IsRunning) return;
            IsRunning = true;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IndexRows.Clear();
            StatusText = "전체 인덱스 스캔 시작...";
            Progress = 0;

            try
            {
                var allDirs = await Task.Run(() => EnumerateAllDirsSafe(RootPath, ct), ct);
                int total = allDirs.Count;
                int done = 0;

                foreach (var dir in allDirs)
                {
                    ct.ThrowIfCancellationRequested();

                    int subDirCount = 0, fileCount = 0;
                    try
                    {
                        subDirCount = Directory.EnumerateDirectories(dir).Take(2000000).Count();
                        fileCount = Directory.EnumerateFiles(dir).Take(2000000).Count();
                    }
                    catch { /* 접근 불가 폴더는 0 처리 */ }

                    IndexRows.Add(new IndexRow(dir, subDirCount, fileCount));

                    done++;
                    Progress = total == 0 ? 1 : (double)done / total;
                    if (done % 200 == 0) StatusText = $"스캔 진행: {done}/{total}";
                }

                StatusText = $"전체 인덱스 완료: {IndexRows.Count} dirs";
                Progress = 1;
            }
            finally
            {
                IsRunning = false;
            }
        }

        private static List<string> EnumerateAllDirsSafe(string root, CancellationToken ct)
        {
            var result = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var cur = stack.Pop();
                result.Add(cur);

                try
                {
                    foreach (var d in Directory.EnumerateDirectories(cur))
                        stack.Push(d);
                }
                catch
                {
                    // 접근 불가면 스킵
                }
            }

            return result;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }
    }

    public sealed class DirItem
    {
        public string Name { get; }
        public string FullPath { get; }
        public DirItem(string name, string fullPath) { Name = name; FullPath = fullPath; }
    }

    public sealed class FileItem
    {
        public string Name { get; }
        public string FullPath { get; }
        public long Length { get; }
        public DateTime LastWriteTime { get; }
        public FileItem(string name, string fullPath, long length, DateTime lastWriteTime)
        { Name = name; FullPath = fullPath; Length = length; LastWriteTime = lastWriteTime; }
    }

    public sealed class IndexRow
    {
        public string FullPath { get; }
        public int SubDirCount { get; }
        public int FileCount { get; }
        public IndexRow(string fullPath, int subDirCount, int fileCount)
        { FullPath = fullPath; SubDirCount = subDirCount; FileCount = fileCount; }
    }
}
