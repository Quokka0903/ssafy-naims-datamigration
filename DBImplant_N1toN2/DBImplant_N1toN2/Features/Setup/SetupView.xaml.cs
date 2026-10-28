using System.Windows;
using System.Windows.Controls;
using DBImplant_N1toN2.DataAccess;

namespace DBImplant_N1toN2.Features.Setup
{
    public partial class SetupView : UserControl
    {
        public SetupView() => InitializeComponent();

        /// <summary>
        /// 이 PC의 ODBC 드라이버/DSN 목록 창을 열어, 고른 항목(Driver={…} / DSN=…)을
        /// 접속문자열에 끼운다(서버·계정 등 나머지 값은 보존).
        /// </summary>
        private void OnBrowseOdbc(object sender, RoutedEventArgs e)
        {
            if (DataContext is not SetupViewModel vm) return;

            var win = new OdbcBrowserWindow { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() == true && !string.IsNullOrEmpty(win.PickedToken))
                vm.ConnectionString = OdbcCatalog.InjectTarget(vm.ConnectionString, win.PickedToken!);
        }
    }
}
