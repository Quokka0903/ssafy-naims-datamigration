using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DBImplant_N1toN2.DataAccess;
using DBImplant_N1toN2.Features.DbBrowser;
using DBImplant_N1toN2.Features.FileBrowser;
using DBImplant_N1toN2.Features.Migration.ViewModels;
using DBImplant_N1toN2.Infrastructure.Commands;

namespace DBImplant_N1toN2.Features.Workspace
{
    public sealed class WorkspaceViewModel : INotifyPropertyChanged
    {
        private readonly ILegacyDbConnection _db;

        public DbBrowserViewModel DbBrowser { get; }
        public FileBrowserViewModel FileBrowser { get; }
        public MigrationPreviewViewModel MigrationPreview { get; }
        public MigrationExecuteViewModel MigrationExecute { get; }

        public string Title { get; }

        public ICommand ShowPreviewCommand { get; }
        public ICommand ShowExecuteCommand { get; }

        public WorkspaceViewModel(ILegacyDbConnection db, string msuRootPath)
        {
            _db = db;
            Title = $"NAIMS Migration — {db.DbTypeName} / {msuRootPath}";

            DbBrowser = new DbBrowserViewModel(db);
            FileBrowser = new FileBrowserViewModel(msuRootPath);
            MigrationPreview = new MigrationPreviewViewModel(db, FileBrowser);
            MigrationExecute = new MigrationExecuteViewModel(db, FileBrowser);

            ShowPreviewCommand = new RelayCommand(() =>
            {
                MigrationPreview.IsVisible = true;
                MigrationPreview.LoadPreviewCommand.Execute(null);
            });
            ShowExecuteCommand = new RelayCommand(() => MigrationExecute.IsVisible = true);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
