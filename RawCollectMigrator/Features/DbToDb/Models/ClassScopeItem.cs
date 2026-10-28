using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RawCollectMigrator.Features.DbToDb
{
    /// <summary>
    /// CLASS(함정급) 단위 선택 이관 UI의 체크박스 한 줄.
    /// IsChecked 가 두 방향 바인딩되므로 전체 선택/해제가 화면에 즉시 반영된다.
    /// </summary>
    public sealed class ClassScopeItem : INotifyPropertyChanged
    {
        public long ClassId { get; init; }
        public string Label { get; init; } = "";

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
