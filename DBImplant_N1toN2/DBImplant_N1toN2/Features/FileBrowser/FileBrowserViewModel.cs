using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using DBImplant_N1toN2.Models;
using static DBImplant_N1toN2.Models.AppSettings;
using DBImplant_N1toN2.Infrastructure;
using DBImplant_N1toN2.Features.Raw.ViewModels;
using DBImplant_N1toN2.Features.NarrowBand.ViewModels;
using DBImplant_N1toN2.Features.ShortPeriod.ViewModels;

namespace DBImplant_N1toN2.Features.FileBrowser
{
    /// <summary>
    /// MSU 파일트리 브라우저: 수집원음/협대역/단주기 3개 카테고리 전환.
    /// 각 카테고리는 기존 TabViewModel을 재활용한다.
    /// </summary>
    public sealed class FileBrowserViewModel : INotifyPropertyChanged
    {
        public string MsuRootPath { get; }

        // ── 카테고리 ─────────────────────────────────────────────────────────
        public string[] Categories { get; } = { "수집원음자료", "협대역원음자료", "단주기원음자료" };

        private string _selectedCategory;
        public string SelectedCategory
        {
            get => _selectedCategory;
            set
            {
                if (Set(ref _selectedCategory, value))
                    SwitchCategory(value);
            }
        }

        // ── 활성 콘텐츠 ──────────────────────────────────────────────────────
        private TabViewModel? _activeContent;
        public TabViewModel? ActiveContent
        {
            get => _activeContent;
            private set => Set(ref _activeContent, value);
        }

        // ── 미리 생성된 자식 VM ───────────────────────────────────────────────
        public RawTabViewModel RawTab { get; }
        public NarrowBandTabViewModel NbTab { get; }
        public ShortPeriodTabViewModel SdTab { get; }

        // ── Ctor ─────────────────────────────────────────────────────────────
        public FileBrowserViewModel(string msuRootPath)
        {
            MsuRootPath = msuRootPath;

            RawTab = new RawTabViewModel("수집원음자료", Roots.CollectRaw);
            NbTab  = new NarrowBandTabViewModel("협대역원음자료", Roots.NarrowbandRaw);
            SdTab  = new ShortPeriodTabViewModel("단주기원음자료", Roots.ShortPeriodRaw);

            _selectedCategory = Categories[0];
            ActiveContent = RawTab;
        }

        private void SwitchCategory(string category)
        {
            ActiveContent = category switch
            {
                "수집원음자료"   => RawTab,
                "협대역원음자료" => NbTab,
                "단주기원음자료" => SdTab,
                _ => null,
            };
        }

        // ── INPC ─────────────────────────────────────────────────────────────
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
