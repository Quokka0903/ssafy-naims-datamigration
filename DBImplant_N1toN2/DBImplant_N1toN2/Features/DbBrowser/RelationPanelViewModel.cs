using System;
using System.ComponentModel;
using System.Data;
using System.Runtime.CompilerServices;

namespace DBImplant_N1toN2.Features.DbBrowser
{
    /// <summary>
    /// 관계 브라우저의 단일 패널 — 하나의 테이블 레코드 목록과 선택 상태를 보유.
    /// 부모 패널에서 선택된 FK 값으로 필터된 결과를 표시한다.
    /// </summary>
    public sealed class RelationPanelViewModel : INotifyPropertyChanged
    {
        // ── Identity ────────────────────────────────────────────────
        public string TableName { get; }

        /// <summary>이 패널의 WHERE 조건 컬럼 (부모 FK → 이 테이블의 컬럼명)</summary>
        public string? FilterColumn { get; }

        /// <summary>부모 패널에서 전달받은 필터 값</summary>
        public object? FilterValue { get; }

        // ── Data ─────────────────────────────────────────────────────
        private DataView? _rows;
        public DataView? Rows
        {
            get => _rows;
            private set => Set(ref _rows, value);
        }

        private DataRowView? _selectedRow;
        public DataRowView? SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (Set(ref _selectedRow, value))
                    SelectedRowChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private string _statusText = "로딩 중...";
        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        // ── Events ───────────────────────────────────────────────────
        /// <summary>SelectedRow가 변경될 때 발생 — RelationBrowserViewModel이 자식 패널을 구성한다.</summary>
        public event EventHandler? SelectedRowChanged;

        // ── Ctor ─────────────────────────────────────────────────────
        public RelationPanelViewModel(string tableName, string? filterColumn, object? filterValue)
        {
            TableName = tableName;
            FilterColumn = filterColumn;
            FilterValue = filterValue;
            StatusText = filterColumn != null
                ? $"{filterColumn} = {filterValue} 로딩 중..."
                : "로딩 중...";
        }

        // ── Data API ─────────────────────────────────────────────────
        public void SetRows(DataTable table)
        {
            Rows = table.DefaultView;
            StatusText = $"{table.Rows.Count} 행";
        }

        public void SetError(string errorMessage)
        {
            Rows = null;
            StatusText = $"오류: {errorMessage}";
        }

        /// <summary>SelectedRow에서 특정 컬럼 값을 꺼낸다.</summary>
        public object? GetColumnValue(string columnName)
        {
            if (SelectedRow == null) return null;
            try
            {
                var val = SelectedRow[columnName];
                return val == DBNull.Value ? null : val;
            }
            catch
            {
                return null;
            }
        }

        // ── INPC ─────────────────────────────────────────────────────
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
