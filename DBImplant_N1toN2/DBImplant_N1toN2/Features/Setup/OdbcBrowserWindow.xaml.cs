using System;
using System.Collections.Generic;
using System.Windows;
using DBImplant_N1toN2.DataAccess;

namespace DBImplant_N1toN2.Features.Setup
{
    /// <summary>
    /// 이 PC에 설치된 ODBC 드라이버·등록된 DSN 을 나열하고, 고른 항목을 접속문자열에 끼울
    /// 토큰(Driver={이름} / DSN=별칭)으로 돌려주는 선택 창.
    /// </summary>
    public partial class OdbcBrowserWindow : Window
    {
        /// <summary>선택 결과 토큰. 취소 시 null.</summary>
        public string? PickedToken { get; private set; }

        private IReadOnlyList<OdbcDsn> _dsns = Array.Empty<OdbcDsn>();

        public OdbcBrowserWindow()
        {
            InitializeComponent();

            var drivers = OdbcCatalog.Drivers();
            _dsns = OdbcCatalog.DataSources();

            HeaderText.Text = $"이 PC({OdbcCatalog.Bitness} 프로세스) 기준 — 드라이버 {drivers.Count}개 · DSN {_dsns.Count}개";
            foreach (var d in drivers) DriverList.Items.Add(d);
            foreach (var s in _dsns) DsnList.Items.Add($"{s.Name}   ({s.Scope} · {s.Driver})");

            if (drivers.Count == 0 && _dsns.Count == 0)
                HeaderText.Text = $"이 PC({OdbcCatalog.Bitness})에 등록된 ODBC 드라이버·DSN 이 없습니다. " +
                                  "비트수가 다르거나(32/64) 드라이버 미설치일 수 있습니다.";
        }

        private void OnDriverPick(object sender, RoutedEventArgs e)
        {
            if (DriverList.SelectedItem is string name)
            {
                PickedToken = "Driver={" + name + "}";
                DialogResult = true;
            }
        }

        private void OnDsnPick(object sender, RoutedEventArgs e)
        {
            int i = DsnList.SelectedIndex;
            if (i >= 0 && i < _dsns.Count)
            {
                PickedToken = "DSN=" + _dsns[i].Name;
                DialogResult = true;
            }
        }

        private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
