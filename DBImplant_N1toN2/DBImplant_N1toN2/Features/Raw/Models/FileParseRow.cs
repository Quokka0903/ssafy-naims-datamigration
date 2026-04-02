using System;

namespace DBImplant_N1toN2.Features.Raw.Models
{
    public sealed record FileParseRow(
        string Kind,
        string FileName,
        string FullPath,
        long Size,
        DateTime LastWriteTime,
        string Summary,
        string? Error);
}
