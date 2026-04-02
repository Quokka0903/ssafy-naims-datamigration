namespace DBImplant_N1toN2.Features.Migration.Models
{
    /// <summary>DB 컬럼 매핑 미리보기 행</summary>
    public sealed class MigrationPreviewRow
    {
        public string TargetTable { get; init; } = "";
        public string TargetColumn { get; init; } = "";
        public string SourceType { get; init; } = "";    // "File" | "LegacyDB" | "Computed" | "Constant"
        public string SourcePath { get; init; } = "";
        public string SampleValue { get; init; } = "";
        public string SqlDataType { get; init; } = "";
        public string Note { get; init; } = "";
    }

    /// <summary>파일 이동 매핑 미리보기 행</summary>
    public sealed class FileTransferPreviewRow
    {
        public string Category { get; init; } = "";       // "수집원음(Raw)" | "협대역(NB)" | "단주기(SD)"
        public string PackageName { get; init; } = "";    // 패키지/임무 폴더명
        public string SourceFile { get; init; } = "";     // 원본 파일 상대경로
        public string FileType { get; init; } = "";       // "WAV" | "LST" | "CLA" | "TMP" | "SET" | "SPS" | "GRM" | "FEA" | "DAT"
        public string TargetTable { get; init; } = "";    // 파싱 결과가 INSERT될 대상 테이블
        public string TargetAction { get; init; } = "";   // "파싱→INSERT" | "BLOB→INSERT" | "경로참조" | "스킵"
        public long FileSize { get; init; }               // 바이트
        public string Note { get; init; } = "";
    }
}
