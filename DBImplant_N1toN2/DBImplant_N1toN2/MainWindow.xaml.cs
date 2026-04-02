using DBImplant_N1toN2.Features.Setup;
using System.Windows;

namespace DBImplant_N1toN2
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new AppViewModel();
        }
    }
}
