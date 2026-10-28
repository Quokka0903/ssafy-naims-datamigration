using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DBImplant_N1toN2.Features.Workspace
{
    /// <summary>
    /// 전역 진행률 오버레이 ViewModel.
    /// 스캔+파싱, DB 읽기, Migration 실행 등 장시간 작업 시 표시.
    /// </summary>
    public sealed class ProgressOverlayViewModel : INotifyPropertyChanged
    {
        private bool _isVisible;
        public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

        private string _title = "";
        public string Title { get => _title; set => Set(ref _title, value); }

        private string _message = "";
        public string Message { get => _message; set => Set(ref _message, value); }

        private double _progress;
        public double Progress { get => _progress; set => Set(ref _progress, value); }

        private bool _isIndeterminate;
        public bool IsIndeterminate { get => _isIndeterminate; set => Set(ref _isIndeterminate, value); }

        public void Show(string title, string message = "", bool indeterminate = false)
        {
            Title = title;
            Message = message;
            Progress = 0;
            IsIndeterminate = indeterminate;
            IsVisible = true;
        }

        public void Update(string message, double progress = -1)
        {
            Message = message;
            if (progress >= 0) { Progress = progress; IsIndeterminate = false; }
        }

        public void Hide()
        {
            IsVisible = false;
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
}
