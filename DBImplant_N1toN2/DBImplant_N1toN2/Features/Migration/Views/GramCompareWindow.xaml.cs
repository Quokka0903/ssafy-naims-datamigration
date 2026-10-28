using System.Windows;

namespace DBImplant_N1toN2.Features.Migration.Views
{
    public partial class GramCompareWindow : Window
    {
        public GramCompareWindow(string grmFilePath)
        {
            InitializeComponent();
            CompareView.LoadGrm(grmFilePath);
        }
    }
}
