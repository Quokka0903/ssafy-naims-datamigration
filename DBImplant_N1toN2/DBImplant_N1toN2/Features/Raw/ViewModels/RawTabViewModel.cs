using DBImplant_N1toN2.Audio.Writer;
using DBImplant_N1toN2.Features.Raw.Models;
using DBImplant_N1toN2.Features.ShortPeriod.Parsers;
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
    public sealed partial class RawTabViewModel : TabViewModel, INotifyPropertyChanged
    {
        private string _rootPath;
        public string RootPath
        {
            get => _rootPath;
            set => Set(ref _rootPath, value);
        }

        public RelayCommand SelectRootPathCommand { get; }

        public ObservableCollection<RawPackageItem> Packages { get; } = new();

        private RawPackageItem? _selectedPackage;
        public RawPackageItem? SelectedPackage
        {
            get => _selectedPackage;
            set => Set(ref _selectedPackage, value);
        }

        private string _statusText = "대기";
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

        private double _progress;
        public double Progress { get => _progress; set => Set(ref _progress, value); }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (!Set(ref _isRunning, value)) return;
                ScanParseCommand.RaiseCanExecuteChanged();
                DbImportCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                SelectRootPathCommand.RaiseCanExecuteChanged();
            }
        }

        public AsyncRelayCommand ScanParseCommand { get; }
        public AsyncRelayCommand DbImportCommand { get; }
        public RelayCommand CancelCommand { get; }

        /// <summary>.grm 파일 비교 전시 윈도우 열기</summary>
        public RelayCommand<object> OpenGramCompareCommand { get; }

        private CancellationTokenSource? _cts;

        public RawTabViewModel(string header, string rootPath) : base(header)
        {
            _rootPath = rootPath;

            ScanParseCommand = new AsyncRelayCommand(ScanParseAllAsync, () => !IsRunning);
            DbImportCommand = new AsyncRelayCommand(DbImportAsync, () => !IsRunning && Packages.Any(p => p.IsChecked));
            CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsRunning);
            SelectRootPathCommand = new RelayCommand(SelectRootPath, () => !IsRunning);
            OpenGramCompareCommand = new RelayCommand<object>(o =>
            {
                if (o is FileParseRow row && row.FullPath.EndsWith(".grm", StringComparison.OrdinalIgnoreCase))
                {
                    var win = new Features.Migration.Views.GramCompareWindow(row.FullPath);
                    win.Show();
                }
            });
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