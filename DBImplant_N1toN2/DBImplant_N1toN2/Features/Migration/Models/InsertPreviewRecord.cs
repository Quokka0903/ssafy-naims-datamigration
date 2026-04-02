using System.Collections.Generic;

namespace DBImplant_N1toN2.Features.Migration.Models
{
    /// <summary>
    /// INSERT될 실제 레코드 1행을 표현.
    /// TargetTable + 컬럼명:값 딕셔너리로 DataGrid에 동적 표시.
    /// </summary>
    public sealed class InsertPreviewRecord
    {
        public string TargetTable { get; init; } = "";
        public string SourcePackage { get; init; } = "";
        public int RowIndex { get; init; }
        public Dictionary<string, string> Columns { get; init; } = new();

        /// <summary>DataGrid 바인딩용 flat properties (주요 컬럼만)</summary>
        public string Col1Name { get; init; } = "";
        public string Col1Value { get; init; } = "";
        public string Col2Name { get; init; } = "";
        public string Col2Value { get; init; } = "";
        public string Col3Name { get; init; } = "";
        public string Col3Value { get; init; } = "";
        public string Col4Name { get; init; } = "";
        public string Col4Value { get; init; } = "";
        public string Col5Name { get; init; } = "";
        public string Col5Value { get; init; } = "";
        public string Col6Name { get; init; } = "";
        public string Col6Value { get; init; } = "";
    }
}
