namespace RawCollectMigrator.Features.Supplement
{
    /// <summary>
    /// 파싱된 <see cref="SupplementSheet"/> 를 UI 바인딩에 맞게 평탄화한 읽기 전용 뷰.
    /// 수집 원음 / 발췌 원음 두 탭이 동일한 DataTemplate 을 공유하기 위한 어댑터.
    /// (세트별로 나뉜 그룹은 SetIndex 컬럼을 가진 flat 리스트로 이어붙여 그리드 하나에 전시한다.)
    /// </summary>
    public sealed class SupplementView
    {
        public SupplementSheet Sheet { get; }
        public string AlgPath { get; }

        public SupplementView(SupplementSheet sheet, string algPath)
        {
            Sheet = sheet;
            AlgPath = algPath;
        }

        public SupplementMissionInfo MissionInfo => Sheet.MissionInfo;
        public int SetCount => Sheet.SetCount;

        public IReadOnlyList<SupplementRecord>      Records      { get; private set; } = Array.Empty<SupplementRecord>();
        public IReadOnlyList<SupplementNaviSeaInfo> NaviSea      { get; private set; } = Array.Empty<SupplementNaviSeaInfo>();
        public IReadOnlyList<SupplementTemperature> Temperatures { get; private set; } = Array.Empty<SupplementTemperature>();
        public IReadOnlyList<SupplementMachine>     Machines     { get; private set; } = Array.Empty<SupplementMachine>();
        public IReadOnlyList<SupplementTarget>      Targets      { get; private set; } = Array.Empty<SupplementTarget>();
        public IReadOnlyList<SupplementNbDetection> NbDetections { get; private set; } = Array.Empty<SupplementNbDetection>();
        public IReadOnlyList<SupplementNbIndex>     NbIndices    { get; private set; } = Array.Empty<SupplementNbIndex>();

        /// <summary>탭 헤더에 붙일 건수 뱃지 문자열.</summary>
        public string RecordsHeader      => $"녹음매체 ({Records.Count})";
        public string NaviSeaHeader      => $"항해·해양 ({NaviSea.Count})";
        public string TemperaturesHeader => $"수온 ({Temperatures.Count})";
        public string MachinesHeader     => $"자함소음 ({Machines.Count})";
        public string TargetsHeader      => $"표적 ({Targets.Count})";
        public string NbDetectionsHeader => $"협대역 탐지 ({NbDetections.Count})";
        public string NbIndicesHeader    => $"협대역 인덱스 ({NbIndices.Count})";

        public static SupplementView? TryLoad(string? algPath)
        {
            if (string.IsNullOrEmpty(algPath)) return null;
            var sheet = AlgReader.TryRead(algPath);
            if (sheet == null) return null;

            var v = new SupplementView(sheet, algPath);
            v.Records      = sheet.Recmedia.SelectMany(r => r.Records).ToList();
            v.NaviSea      = sheet.NaviSeaInfos.ToList();
            v.Temperatures = sheet.NaviSeaInfos.SelectMany(n => n.Temperatures).ToList();
            v.Machines     = sheet.NoiseInfos.SelectMany(n => n.Machines).ToList();
            v.Targets      = sheet.TargetInfos.SelectMany(t => t.Targets).ToList();
            v.NbDetections = sheet.NbDetectionInfos.SelectMany(d => d.NbDetections).ToList();
            v.NbIndices    = v.NbDetections.SelectMany(d => d.Indices).ToList();
            return v;
        }
    }
}
