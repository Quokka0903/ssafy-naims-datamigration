using System.IO;

namespace RawCollectMigrator.Features.DbToDb
{
    /// <summary>
    /// 한 폴더의 "파일명 → 전체경로" 인덱스.
    /// SMB 공유에서 파일 하나마다 재귀 탐색하면 매우 느리므로, 폴더당 1회만 훑어 캐시한다.
    /// 같은 이름이 여러 곳에 있으면 얕은 경로를 우선한다 — BFS(레벨 순회) + 레벨 내 이름순 정렬로
    /// 명시적으로 보장한다(Directory.EnumerateFiles 의 열거 순서는 비보장).
    ///
    /// ⚠ 단일 스레드 전제: 인스턴스는 불변이지만, 이를 캐시하는 SnippetFileResolver 와 함께
    ///   "한 번에 한 스레드"에서만 쓰는 것을 전제로 한다(실행기·프리플라이트·파일대조 모두 순차 사용).
    /// </summary>
    public sealed class FileIndex
    {
        /// <summary>실패 사유 상세 보존 상한(그 이상은 개수만 센다).</summary>
        private const int MaxFailureDetail = 8;

        public string Root { get; }
        public int FileCount => _byName.Count;
        public bool RootExists { get; }

        /// <summary>
        /// 열거 중 일부 폴더를 읽지 못했으면 true.
        /// 이때 "인덱스에 없음"은 "파일이 없음"을 뜻하지 않는다(실제로는 존재할 수 있음).
        /// </summary>
        public bool PartialFailure => FailedDirCount > 0;

        /// <summary>열거에 실패한 폴더 수.</summary>
        public int FailedDirCount { get; }

        /// <summary>실패 사유(폴더별, 최대 <see cref="MaxFailureDetail"/>건 보존).</summary>
        public IReadOnlyList<string> FailureReasons => _failures;

        /// <summary>인덱스 구축 시각(UTC) — 부분 실패 인덱스의 재시도 쿨다운 판정용.</summary>
        public DateTime BuiltAtUtc { get; }

        /// <summary>부분 실패 요약 문구 (진단·로그용). 실패가 없으면 빈 문자열.</summary>
        public string FailureSummary => !PartialFailure
            ? ""
            : $"폴더 {FailedDirCount}개 열거 실패 (첫 사유: {_failures.FirstOrDefault() ?? "?"})";

        private readonly Dictionary<string, string> _byName;
        private readonly List<string> _failures;

        private FileIndex(string root, bool exists, Dictionary<string, string> map,
            List<string> failures, int failedDirCount)
        {
            Root = root;
            RootExists = exists;
            _byName = map;
            _failures = failures;
            FailedDirCount = failedDirCount;
            BuiltAtUtc = DateTime.UtcNow;
        }

        public static FileIndex Build(string root, CancellationToken ct = default)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var failures = new List<string>();
            int failedDirs = 0;

            // Directory.Exists 는 네트워크 오류 시 예외 없이 false 를 돌려준다.
            bool exists = Directory.Exists(root);
            if (exists)
            {
                // BFS 수동 재귀: 레벨 순회라 "얕은 경로 우선"이 구조적으로 보장되고,
                // 접근 불가 하위 폴더는 그 폴더만 건너뛰되 실패 사실을 기록한다.
                // (기존 Directory.EnumerateFiles(RecurseSubdirectories) 방식은 중간 예외 시
                //  전체 열거가 끊겨 "부분 인덱스"가 조용히 만들어졌고 — SMB 일시 장애 때
                //  존재하는 파일이 '없음'으로 오판되어 이관이 침묵 누락됐다.)
                var queue = new Queue<DirectoryInfo>();
                queue.Enqueue(new DirectoryInfo(root));
                while (queue.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var dir = queue.Dequeue();

                    FileSystemInfo[] entries;
                    try
                    {
                        // 폴더당 1회 왕복으로 파일+하위폴더를 함께 얻는다(SMB 왕복 최소화).
                        // 속성은 열거 결과에 이미 실려 와 추가 왕복이 없다.
                        entries = dir.GetFileSystemInfos();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failedDirs++;
                        if (failures.Count < MaxFailureDetail)
                            failures.Add($"{dir.FullName}: {ex.Message}");
                        continue;
                    }

                    // 레벨 내에서도 이름순 정렬 — 어떤 동명 파일이 이기는지 결정적으로 만든다.
                    Array.Sort(entries, static (a, b) =>
                        StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

                    foreach (var e in entries)
                    {
                        // 기존 AttributesToSkip = FileAttributes.System 유지
                        if ((e.Attributes & FileAttributes.System) != 0) continue;
                        if (e is DirectoryInfo sub) queue.Enqueue(sub);
                        else if (!map.ContainsKey(e.Name)) map[e.Name] = e.FullName;
                    }
                }
            }

            return new FileIndex(root, exists, map, failures, failedDirs);
        }

        public bool TryGet(string fileName, out string fullPath) => _byName.TryGetValue(fileName, out fullPath!);

        /// <summary>인덱스에 있으나 참조되지 않은 파일(고아) 열거.</summary>
        public IEnumerable<string> PathsExcept(HashSet<string> referencedNames)
            => _byName.Where(kv => !referencedNames.Contains(kv.Key)).Select(kv => kv.Value);
    }

    /// <summary>
    /// 스니펫/영상 파일을 지정된 폴더에서 찾는다.
    ///
    /// [NAIMS-ISSUE:SNIPPET-ROOTS] 파일 출처는 CLASSIFICATIONDB 한 곳으로 고정한다 (2026-08-18 변경).
    ///   \...\MSU\CLASSIFICATIONDB\{SNIPPET | IMAGE | CTRYIMAGE}
    /// 예전에는 단주기·협대역 원음 폴더까지 2·3순위로 폴백 탐색했는데,
    ///   · 같은 파일명이 원음 폴더에도 있어 엉뚱한 파일을 집을 수 있고
    ///   · 적재된 blob 이 "어느 폴더에서 왔는지" 사후에 알 수 없어
    /// 단일 루트로 좁혔다. 구조상 여러 루트를 받을 수 있게 두었으나(진단·이행 목적),
    /// 운영 경로는 항상 원소 1개다.
    ///
    /// 인덱스는 루트별로 1회만 만들고 재사용한다.
    /// 부분 실패한 인덱스(SMB 일시 장애 등)는 영구 캐시하지 않고 쿨다운 후 재구축을 시도한다.
    ///
    /// ⚠ 단일 스레드 전제: _cache 는 잠금 없이 쓴다. 실행기(행 순차 처리)·프리플라이트·
    ///   파일대조(UI 의 Task.Run 1개) 모두 한 번에 한 스레드에서만 호출한다.
    ///   병렬 호출로 바꾸려면 이 캐시부터 재설계해야 한다.
    /// </summary>
    public sealed class SnippetFileResolver
    {
        public sealed record RootSpec(string Label, string Path);

        /// <summary>부분 실패 인덱스 재시도 쿨다운 — 대형 폴더의 지나친 전체 재열거를 막는다.</summary>
        private static readonly TimeSpan PartialRetryCooldown = TimeSpan.FromSeconds(60);

        private readonly List<RootSpec> _roots;
        private readonly Dictionary<string, FileIndex> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Action<string> _log;

        public SnippetFileResolver(IEnumerable<RootSpec> roots, Action<string>? log = null)
        {
            _roots = roots.Where(r => !string.IsNullOrWhiteSpace(r.Path)).ToList();
            _log = log ?? (_ => { });
        }

        /// <summary>
        /// 탐색 결과. IndexIncomplete 는 못 찾았을 때(Found=false) 탐색한 인덱스 중
        /// 부분 실패가 있으면 true — 파일이 실제로는 존재하는데 인덱스에 못 들어갔을 수 있다는 뜻.
        /// </summary>
        public sealed record Resolved(bool Found, string FullPath, long Size, string SourceLabel,
            bool IndexIncomplete = false);

        /// <summary>
        /// 우선순위 순서대로 파일을 찾는다. 앞 순위에서 찾으면 뒤 순위 인덱스는 만들지 않는다.
        /// </summary>
        public Resolved Resolve(string fileName, CancellationToken ct = default)
        {
            bool anyPartial = false;
            foreach (var r in _roots)
            {
                var idx = GetIndex(r.Path, ct);
                anyPartial |= idx.PartialFailure;
                if (idx.TryGet(fileName, out var path))
                {
                    long size = 0;
                    try { size = new FileInfo(path).Length; } catch { }
                    return new Resolved(true, path, size, r.Label);
                }
            }

            // 못 찾음 — 1순위 기준 경로를 참고용으로 돌려준다.
            var first = _roots.FirstOrDefault();
            var guess = first == null ? fileName : Path.Combine(first.Path, fileName);
            return new Resolved(false, guess, 0, "", anyPartial);
        }

        public FileIndex GetIndex(string root, CancellationToken ct = default)
        {
            if (_cache.TryGetValue(root, out var idx))
            {
                // 온전한 인덱스는 그대로 재사용. 부분 실패 인덱스는 영구 캐시하지 않고
                // 쿨다운이 지나면 재구축을 시도한다 — SMB 일시 장애로 만들어진
                // "가짜 없음" 판정이 실행 내내 굳는 것을 막는다.
                if (!idx.PartialFailure || DateTime.UtcNow - idx.BuiltAtUtc < PartialRetryCooldown)
                    return idx;
                _log($"[파일인덱스] 부분 인덱스 재구축 시도: {root}");
            }

            idx = FileIndex.Build(root, ct);
            if (idx.PartialFailure)
                _log($"[파일인덱스] ⚠ 부분 인덱스: {root} — {idx.FailureSummary}"
                     + " (인덱스에 없는 파일이 실제로는 존재할 수 있음)");
            else if (!idx.RootExists)
                _log($"[파일인덱스] 루트 접근 불가: {root}");
            _cache[root] = idx;
            return idx;
        }

        /// <summary>구축된 인덱스 중 부분 실패가 하나라도 있는지 (진단용).</summary>
        public bool AnyPartialIndex => _cache.Values.Any(i => i.PartialFailure);

        /// <summary>이미 구축된 인덱스들의 요약 (진단 표시용). 부분 실패도 침묵하지 않고 드러낸다.</summary>
        public string DescribeLoaded()
        {
            if (_cache.Count == 0) return "";
            var parts = _roots
                .Where(r => _cache.ContainsKey(r.Path))
                .Select(r =>
                {
                    var i = _cache[r.Path];
                    if (!i.RootExists) return $"{r.Label} (접근 불가)";
                    return i.PartialFailure
                        ? $"{r.Label} {i.FileCount}개 ⚠부분({i.FailureSummary})"
                        : $"{r.Label} {i.FileCount}개";
                });
            return string.Join(" · ", parts);
        }

        /// <summary>1순위 루트(고아 파일 판정 기준).</summary>
        public RootSpec? PrimaryRoot => _roots.FirstOrDefault();
    }
}
