using System.IO;

namespace RawCollectMigrator.Features.DbToDb
{
    public enum FileState
    {
        /// <summary>DB가 참조하고 실제 파일도 있음.</summary>
        Found,
        /// <summary>DB가 참조하지만 파일이 없음 — 이관 시 해당 스니펫 데이터 유실.</summary>
        Missing,
        /// <summary>디스크에 있으나 DB가 참조하지 않음.</summary>
        Orphan
    }

    /// <summary>
    /// 스니펫/영상 테이블이 참조하는 외부 파일 한 건의 대조 결과.
    /// DB에는 파일명만 있으므로(예: nb_CDMU_56812750.grm) 지정 폴더에서 실제 파일을 찾아 확인한다.
    /// </summary>
    public sealed class SnippetFileRow
    {
        public string RecordId  { get; init; } = "";
        public string Kind      { get; init; } = "";   // GRAM / AUDIO / IMAGE / FLAG
        public string FileName  { get; init; } = "";
        public string FullPath  { get; init; } = "";
        public long   SizeBytes { get; init; }
        public FileState State  { get; init; }

        /// <summary>어느 폴더에서 찾았는지 (운영 경로에서는 항상 "CLASSIFICATION").</summary>
        public string SourceLabel { get; init; } = "";

        public bool Exists => State != FileState.Missing;

        public string StateKey => State.ToString();
        public string StateText => State switch
        {
            FileState.Found   => "✓ 있음",
            FileState.Missing => "✗ 없음",
            FileState.Orphan  => "◇ 고아",
            _ => ""
        };

        public string SizeText => State == FileState.Missing ? "-" : FormatBytes(SizeBytes);

        private static string FormatBytes(long b)
        {
            if (b < 1024) return $"{b} B";
            if (b < 1024L * 1024) return $"{b / 1024.0:F1} KB";
            if (b < 1024L * 1024 * 1024) return $"{b / 1024.0 / 1024:F1} MB";
            return $"{b / 1024.0 / 1024 / 1024:F2} GB";
        }

        /// <summary>
        /// 지정된 폴더(CLASSIFICATIONDB 하위)에서 파일을 찾는다.
        /// </summary>
        public static SnippetFileRow Probe(
            string recordId, string kind, string? fileName,
            SnippetFileResolver resolver, CancellationToken ct = default)
        {
            fileName = (fileName ?? "").Trim();
            if (string.IsNullOrEmpty(fileName))
            {
                return new SnippetFileRow
                {
                    RecordId = recordId, Kind = kind,
                    FileName = "(파일명 없음)", State = FileState.Missing
                };
            }

            var r = resolver.Resolve(fileName, ct);
            return new SnippetFileRow
            {
                RecordId = recordId,
                Kind = kind,
                FileName = fileName,
                FullPath = r.FullPath,
                SizeBytes = r.Size,
                SourceLabel = r.SourceLabel,
                State = r.Found ? FileState.Found : FileState.Missing
            };
        }

        /// <summary>디스크에만 있고 DB가 참조하지 않는 파일.</summary>
        public static SnippetFileRow Orphan(string fullPath, string sourceLabel)
        {
            long size = 0;
            string name = fullPath;
            string ext = "";
            try
            {
                var fi = new FileInfo(fullPath);
                size = fi.Length;
                name = fi.Name;
                ext = fi.Extension.TrimStart('.').ToUpperInvariant();
            }
            catch { }

            return new SnippetFileRow
            {
                RecordId = "-",
                Kind = ext,
                FileName = name,
                FullPath = fullPath,
                SizeBytes = size,
                SourceLabel = sourceLabel,
                State = FileState.Orphan
            };
        }
    }
}
