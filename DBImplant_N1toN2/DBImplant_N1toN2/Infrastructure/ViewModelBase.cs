namespace DBImplant_N1toN2.Infrastructure
{
    public abstract class TabViewModel
    {
        public string Header { get; }
        protected TabViewModel(string header) => Header = header;
    }
}
