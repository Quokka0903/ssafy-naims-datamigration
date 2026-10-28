using System.ComponentModel;
using System.Runtime.CompilerServices;
using DBImplant_N1toN2.Features.Workspace;

namespace DBImplant_N1toN2.Features.Setup
{
    public sealed class AppViewModel : INotifyPropertyChanged
    {
        private object _currentView;

        public object CurrentView
        {
            get => _currentView;
            set => Set(ref _currentView, value);
        }

        private string _statusText = "준비";
        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        public AppViewModel()
        {
            var setup = new SetupViewModel();
            setup.MigrationReady += OnMigrationReady;
            _currentView = setup;
        }

        private void OnMigrationReady(object? sender, WorkspaceViewModel workspace)
        {
            CurrentView = workspace;
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
