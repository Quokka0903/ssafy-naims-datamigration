using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DBImplant_N1toN2.DataAccess;
using DBImplant_N1toN2.Features.FileBrowser;
using DBImplant_N1toN2.Features.Migration.Models;
using DBImplant_N1toN2.Infrastructure.Commands;

namespace DBImplant_N1toN2.Features.Migration.ViewModels
{
    public sealed class MigrationPreviewViewModel : INotifyPropertyChanged
    {
        private readonly ILegacyDbConnection _db;
        private readonly FileBrowserViewModel _fileBrowser;

        // ── Visibility ───────────────────────────────────────────────────
        private bool _isVisible;
        public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

        private bool _isLoading;
        public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

        // ── DB 매핑 탭 ───────────────────────────────────────────────────
        public ObservableCollection<MigrationPreviewRow> DbRows { get; } = new();
        public ObservableCollection<MigrationPreviewRow> DbFilteredRows { get; } = new();

        private string[] _dbAvailableTables = Array.Empty<string>();
        public string[] DbAvailableTables { get => _dbAvailableTables; private set => Set(ref _dbAvailableTables, value); }

        private string? _dbSelectedTable;
        public string? DbSelectedTable
        {
            get => _dbSelectedTable;
            set { if (Set(ref _dbSelectedTable, value)) RebuildDbFiltered(); }
        }

        // ── 파일 이동 탭 ─────────────────────────────────────────────────
        public ObservableCollection<FileTransferPreviewRow> FileRows { get; } = new();
        public ObservableCollection<FileTransferPreviewRow> FileFilteredRows { get; } = new();

        private string[] _fileCategories = Array.Empty<string>();
        public string[] FileCategories { get => _fileCategories; private set => Set(ref _fileCategories, value); }

        private string? _fileSelectedCategory;
        public string? FileSelectedCategory
        {
            get => _fileSelectedCategory;
            set { if (Set(ref _fileSelectedCategory, value)) RebuildFileFiltered(); }
        }

        // ── 파일 이동 트리 뷰 ────────────────────────────────────────────
        public ObservableCollection<FileTransferTreeNode> FileTree { get; } = new();

        // ── 통계 ─────────────────────────────────────────────────────────
        private string _dbSummary = "";
        public string DbSummary { get => _dbSummary; private set => Set(ref _dbSummary, value); }

        private string _fileSummary = "";
        public string FileSummary { get => _fileSummary; private set => Set(ref _fileSummary, value); }

        // ── INSERT 미리보기 탭 (실제 파싱 데이터 → 레코드) ─────────────
        public ObservableCollection<InsertPreviewRecord> InsertRows { get; } = new();
        public ObservableCollection<InsertPreviewRecord> InsertFilteredRows { get; } = new();

        private string[] _insertAvailableTables = Array.Empty<string>();
        public string[] InsertAvailableTables { get => _insertAvailableTables; private set => Set(ref _insertAvailableTables, value); }

        private string? _insertSelectedTable;
        public string? InsertSelectedTable
        {
            get => _insertSelectedTable;
            set { if (Set(ref _insertSelectedTable, value)) RebuildInsertFiltered(); }
        }

        private string _insertSummary = "";
        public string InsertSummary { get => _insertSummary; private set => Set(ref _insertSummary, value); }

        // ── Commands ─────────────────────────────────────────────────────
        public AsyncRelayCommand LoadPreviewCommand { get; }
        public RelayCommand CloseCommand { get; }

        // ── Ctor ─────────────────────────────────────────────────────────
        public MigrationPreviewViewModel(ILegacyDbConnection db, FileBrowserViewModel fileBrowser)
        {
            _db = db;
            _fileBrowser = fileBrowser;
            LoadPreviewCommand = new AsyncRelayCommand(LoadPreviewAsync);
            CloseCommand = new RelayCommand(() => IsVisible = false);
        }

        // ── 로드 ─────────────────────────────────────────────────────────
        private async Task LoadPreviewAsync()
        {
            IsLoading = true;
            try
            {
                var (dbRows, fileRows, insertRows) = await Task.Run(() =>
                {
                    var db = MigrationMappingRegistry.BuildDbPreview(_db, _fileBrowser);
                    var file = MigrationMappingRegistry.BuildFilePreview(_fileBrowser);
                    var insert = InsertPreviewBuilder.Build(_fileBrowser);
                    return (db, file, insert);
                });

                // DB 매핑
                DbRows.Clear();
                foreach (var r in dbRows) DbRows.Add(r);
                DbAvailableTables = dbRows.Select(r => r.TargetTable).Distinct().OrderBy(t => t).ToArray();
                DbSelectedTable = null;
                RebuildDbFiltered();
                DbSummary = $"{dbRows.Count}개 매핑 — {DbAvailableTables.Length}개 테이블";

                // 파일 이동 (flat list)
                FileRows.Clear();
                foreach (var r in fileRows) FileRows.Add(r);
                FileCategories = fileRows.Select(r => r.Category).Distinct().OrderBy(c => c).ToArray();
                FileSelectedCategory = null;
                RebuildFileFiltered();

                long totalSize = fileRows.Sum(r => r.FileSize);
                int insertCount = fileRows.Count(r => r.TargetAction.Contains("INSERT") || r.TargetAction.Contains("변환"));
                int skipCount = fileRows.Count(r => r.TargetAction.Contains("스킵"));
                FileSummary = $"{fileRows.Count}개 파일 — INSERT/변환: {insertCount}, 스킵: {skipCount}, 총 {totalSize / 1024.0 / 1024.0:F1} MB";

                // 파일 이동 트리 빌드 (카테고리 → 임무/패키지 → 대상테이블 → 파일)
                FileTree.Clear();
                BuildFileTree(fileRows);

                // INSERT 미리보기
                InsertRows.Clear();
                foreach (var r in insertRows) InsertRows.Add(r);
                InsertAvailableTables = insertRows.Select(r => r.TargetTable).Distinct().OrderBy(t => t).ToArray();
                InsertSelectedTable = null;
                RebuildInsertFiltered();
                InsertSummary = $"{insertRows.Count}개 레코드 — {InsertAvailableTables.Length}개 테이블";
            }
            catch (Exception ex)
            {
                App.WriteLog($"Preview 로드 오류: {ex}");
                DbSummary = $"오류: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void RebuildDbFiltered()
        {
            DbFilteredRows.Clear();
            var src = string.IsNullOrEmpty(_dbSelectedTable)
                ? DbRows : new ObservableCollection<MigrationPreviewRow>(DbRows.Where(r => r.TargetTable == _dbSelectedTable));
            foreach (var r in src) DbFilteredRows.Add(r);
        }

        private void RebuildInsertFiltered()
        {
            InsertFilteredRows.Clear();
            var src = string.IsNullOrEmpty(_insertSelectedTable)
                ? InsertRows : new ObservableCollection<InsertPreviewRecord>(InsertRows.Where(r => r.TargetTable == _insertSelectedTable));
            foreach (var r in src) InsertFilteredRows.Add(r);
        }

        private void RebuildFileFiltered()
        {
            FileFilteredRows.Clear();
            var src = string.IsNullOrEmpty(_fileSelectedCategory)
                ? FileRows : new ObservableCollection<FileTransferPreviewRow>(FileRows.Where(r => r.Category == _fileSelectedCategory));
            foreach (var r in src) FileFilteredRows.Add(r);
        }

        // ── 파일 이동 트리 빌드 ──────────────────────────────────────────
        private void BuildFileTree(List<FileTransferPreviewRow> rows)
        {
            // 계층: Category → PackageName → TargetTable → 파일
            var byCategory = rows.GroupBy(r => r.Category).OrderBy(g => g.Key);
            foreach (var catGroup in byCategory)
            {
                var catNode = new FileTransferTreeNode
                {
                    DisplayName = catGroup.Key,
                    NodeType = "Category",
                    Icon = "📂",
                    FileCount = catGroup.Count(),
                    TotalSize = catGroup.Sum(r => r.FileSize),
                    Detail = $"{catGroup.Count()}개 파일, {catGroup.Sum(r => r.FileSize) / 1024.0 / 1024.0:F1} MB"
                };

                var byMission = catGroup.GroupBy(r => r.PackageName).OrderBy(g => g.Key);
                foreach (var missionGroup in byMission)
                {
                    var missionNode = new FileTransferTreeNode
                    {
                        DisplayName = missionGroup.Key,
                        NodeType = "Mission",
                        Icon = "📁",
                        FileCount = missionGroup.Count(),
                        TotalSize = missionGroup.Sum(r => r.FileSize),
                        Detail = $"{missionGroup.Count()}개"
                    };

                    var byTable = missionGroup.GroupBy(r => r.TargetTable).OrderBy(g => g.Key);
                    foreach (var tableGroup in byTable)
                    {
                        var tableNode = new FileTransferTreeNode
                        {
                            DisplayName = tableGroup.Key,
                            NodeType = "Table",
                            Icon = "🗄",
                            FileCount = tableGroup.Count(),
                            TotalSize = tableGroup.Sum(r => r.FileSize),
                            Action = tableGroup.First().TargetAction,
                            Detail = $"{tableGroup.Count()}개, {tableGroup.First().TargetAction}"
                        };

                        foreach (var file in tableGroup)
                        {
                            tableNode.Children.Add(new FileTransferTreeNode
                            {
                                DisplayName = file.SourceFile,
                                NodeType = "File",
                                Icon = file.FileType switch
                                {
                                    "WAV" => "🎵",
                                    "GRM" => "📊",
                                    "TXT" or "LST" or "SET" => "📄",
                                    "DAT" => "📈",
                                    "DIR" => "📁",
                                    _ => "📎"
                                },
                                Action = file.TargetAction,
                                TotalSize = file.FileSize,
                                Detail = file.Note
                            });
                        }

                        missionNode.Children.Add(tableNode);
                    }

                    catNode.Children.Add(missionNode);
                }

                FileTree.Add(catNode);
            }
        }

        // ── INPC ─────────────────────────────────────────────────────────
        public event PropertyChangedEventHandler? PropertyChanged;
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }
    }
}
