using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DBImplant_N1toN2.Infrastructure.Utilities
{
    public class FolderNode : INotifyPropertyChanged
    {
        public string FullPath { get; }
        public string DisplayName { get; }

        public ObservableCollection<FolderNode> Children { get; } = new();

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (!Set(ref _isExpanded, value)) return;
                if (_isExpanded) _ = EnsureChildrenLoadedAsync();
            }
        }

        private bool _childrenLoaded;
        private readonly Func<FolderNode, CancellationToken, Task> _loadChildrenAsync;
        private readonly CancellationTokenSource _cts = new();

        // addDummyChild / displayNameOverride 추가
        public FolderNode(
            string fullPath,
            bool isRoot,
            Func<FolderNode, CancellationToken, Task> loadChildrenAsync,
            bool addDummyChild = true,
            string? displayNameOverride = null)
        {
            FullPath = fullPath;

            DisplayName = displayNameOverride
                          ?? (isRoot
                              ? fullPath
                              : Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar)));

            _loadChildrenAsync = loadChildrenAsync;

            // 더미는 더미를 추가하지 않도록 차단
            if (addDummyChild && MightHaveChildDir(fullPath))
                Children.Add(new FolderNodeDummy());
        }

        private static bool MightHaveChildDir(string path)
        {
            try { return Directory.EnumerateDirectories(path).Take(1).Any(); }
            catch { return false; }
        }

        private async Task EnsureChildrenLoadedAsync()
        {
            if (_childrenLoaded) return;
            _childrenLoaded = true;

            // 더미 제거 후 로드
            if (Children.Count == 1 && Children[0] is FolderNodeDummy)
                Children.Clear();

            await _loadChildrenAsync(this, _cts.Token);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }

        private sealed class FolderNodeDummy : FolderNode
        {
            // ✅ addDummyChild=false 로 재귀 차단
            public FolderNodeDummy()
                : base(
                    fullPath: "",
                    isRoot: false,
                    loadChildrenAsync: (_, __) => Task.CompletedTask,
                    addDummyChild: false,
                    displayNameOverride: "(loading...)")
            { }
        }
    }
}
