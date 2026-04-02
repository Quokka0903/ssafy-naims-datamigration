using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DBImplant_N1toN2.Infrastructure.Commands;
using DBImplant_N1toN2.DataAccess;

namespace DBImplant_N1toN2.Features.DbBrowser
{
    /// <summary>
    /// 레거시 DB FK 체인 드릴다운 브라우저.
    /// ILegacyDbConnection을 통해 Oracle/MsSQL 공통 처리.
    /// </summary>
    public sealed class DbBrowserViewModel : INotifyPropertyChanged
    {
        private readonly ILegacyDbConnection _db;
        private readonly Dictionary<string, FkRelation[]> _fkSchema;

        // ── 루트 테이블 ──────────────────────────────────────────────────────
        public string[] RootTableNames { get; }

        private string? _selectedRootTable;
        public string? SelectedRootTable
        {
            get => _selectedRootTable;
            set
            {
                if (Set(ref _selectedRootTable, value) && value != null)
                    _ = LoadRootAsync();
            }
        }

        // ── 패널 체인 ────────────────────────────────────────────────────────
        public ObservableCollection<RelationPanelViewModel> Panels { get; } = new();

        // ── 상태 ─────────────────────────────────────────────────────────────
        private string _statusText = "좌측에서 루트 테이블을 선택하세요.";
        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        public string DbTypeName => _db.DbTypeName;

        // ── Ctor ─────────────────────────────────────────────────────────────
        public DbBrowserViewModel(ILegacyDbConnection db)
        {
            _db = db;
            _fkSchema = db.GetFkSchema();
            RootTableNames = db.GetRootTableNames();
        }

        // ── 루트 로드 ────────────────────────────────────────────────────────
        private async Task LoadRootAsync()
        {
            Panels.Clear();
            if (SelectedRootTable == null) return;
            await LoadTableAsync(SelectedRootTable, null, null);
        }

        private async Task LoadTableAsync(string tableName, string? filterColumn, object? filterValue)
        {
            var panel = new RelationPanelViewModel(tableName, filterColumn, filterValue);
            panel.SelectedRowChanged += OnPanelSelectionChanged;
            Panels.Add(panel);

            StatusText = $"{tableName} 로딩 중...";
            try
            {
                var dt = await Task.Run(() => _db.QueryTable(tableName, filterColumn, filterValue));
                panel.SetRows(dt);
                StatusText = $"{tableName}: {dt.Rows.Count} 행";
            }
            catch (Exception ex)
            {
                panel.SetError(ex.Message);
                StatusText = $"오류 ({tableName}): {ex.Message}";
            }
        }

        // ── FK 캐스케이드 ────────────────────────────────────────────────────
        private async void OnPanelSelectionChanged(object? sender, EventArgs e)
        {
            if (sender is not RelationPanelViewModel panel) return;

            int idx = Panels.IndexOf(panel);
            if (idx < 0) return;
            while (Panels.Count > idx + 1)
                Panels.RemoveAt(Panels.Count - 1);

            if (panel.SelectedRow == null) return;
            if (!_fkSchema.TryGetValue(panel.TableName, out var relations)) return;

            foreach (var rel in relations)
            {
                var pkValue = panel.GetColumnValue(rel.PkColumn);
                if (pkValue == null) continue;
                await LoadTableAsync(rel.ChildTable, rel.ChildFkColumn, pkValue);
            }
        }

        // ── INPC ─────────────────────────────────────────────────────────────
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
