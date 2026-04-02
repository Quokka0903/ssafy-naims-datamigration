using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace DBImplant_N1toN2.Features.Raw.Parsers
{
    public enum IssueSeverity { Info, Warning, Error }
    public sealed record Issue(IssueSeverity Severity, string Message);

    // ========================= 결과 모델 =========================

    public sealed record RootScanResult(
        string RootPath,
        IReadOnlyList<CollectionFolder> Collections);

    public sealed record CollectionFolder(
        string DirectoryPath,
        IReadOnlyList<ShortPeriodSdPackage> SdPackages,
        IReadOnlyList<NarrowBandFolder> NbFolders,
        IReadOnlyList<Issue> Issues);

    public sealed record ShortPeriodSdPackage(
        string DirectoryPath,
        IReadOnlyList<string> WavFiles,
        string? SpsListPath,
        IReadOnlyList<Issue> Issues);

    public sealed record NarrowBandFolder(
        string DirectoryPath,
        IReadOnlyList<NarrowBandGroup> Groups,
        IReadOnlyList<string> ExtraFiles,
        IReadOnlyList<Issue> Issues);

    public sealed record NarrowBandGroup(
        string BaseName,
        string WavPath,
        string? DemonGrmPath,
        string? DemonDrawingListPath,
        string? DetectDatPath,
        string? EventGrmPath,
        string? LofarGrmPath,
        string? LofarDrawingListPath,
        string? NbListPath,
        string? SpsListPath,
        IReadOnlyList<string> ExtraFiles,
        IReadOnlyList<Issue> Issues);

    // ========================= 스캐너(V2) =========================

    public static class RawScanner
    {
        /// <summary>
        /// 수집원음자료(root) 아래의 "직계 하위 폴더"들을 Collection으로 스캔
        /// </summary>
        public static RootScanResult ScanCollections(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new ArgumentException("rootPath is null/empty.", nameof(rootPath));
            if (!Directory.Exists(rootPath))
                throw new DirectoryNotFoundException(rootPath);

            var collections = new List<CollectionFolder>();

            foreach (var colDir in SafeEnumerateDirectories(rootPath))
            {
                collections.Add(ScanOneCollection(colDir));
            }

            return new RootScanResult(rootPath, collections);
        }

        /// <summary>
        /// UI 프리징 방지용: 컬렉션 단위로 스트리밍
        /// </summary>
        public static async IAsyncEnumerable<CollectionFolder> EnumerateCollectionsAsync(
            string rootPath,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!Directory.Exists(rootPath))
                throw new DirectoryNotFoundException(rootPath);

            foreach (var colDir in SafeEnumerateDirectories(rootPath))
            {
                ct.ThrowIfCancellationRequested();
                yield return ScanOneCollection(colDir);
                await Task.Yield();
            }
        }

        // ========================= Collection 스캔 =========================

        private static CollectionFolder ScanOneCollection(string collectionDir)
        {
            var issues = new List<Issue>();

            var (sdDirs, nbDirs) = FindPackagesInsideCollection(collectionDir);

            if (sdDirs.Count == 0 && nbDirs.Count == 0)
                issues.Add(new Issue(IssueSeverity.Info, "내부에서 .sd 폴더와 협대역 폴더를 찾지 못함(가능한 케이스)."));

            var sdPkgs = sdDirs.Select(ScanSdFolder).ToList();
            var nbFolders = nbDirs.Select(ScanNarrowBandFolder).ToList();

            return new CollectionFolder(collectionDir, sdPkgs, nbFolders, issues);
        }

        /// <summary>
        /// collectionDir 내부를 DFS로 훑어서:
        /// - *.sd 폴더
        /// - '협대역' 폴더
        /// 를 찾아낸다.
        /// 찾은 폴더는 패키지 루트로 보고 그 아래로는 재탐색하지 않는다.
        /// </summary>
        private static (List<string> SdDirs, List<string> NbDirs) FindPackagesInsideCollection(string collectionDir)
        {
            var sdDirs = new List<string>();
            var nbDirs = new List<string>();

            var stack = new Stack<string>();
            stack.Push(collectionDir);

            while (stack.Count > 0)
            {
                var cur = stack.Pop();

                foreach (var child in SafeEnumerateDirectories(cur))
                {
                    var name = Path.GetFileName(child.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                    if (name.EndsWith(".sd", StringComparison.OrdinalIgnoreCase))
                    {
                        sdDirs.Add(child);
                        continue; // 패키지 루트: 더 내려가지 않음
                    }

                    if (IsNarrowBandFolderName(name))
                    {
                        nbDirs.Add(child);
                        continue; // 패키지 루트: 더 내려가지 않음
                    }

                    stack.Push(child);
                }
            }

            // 중복 제거(경로 표기 차이 대비)
            sdDirs = sdDirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            nbDirs = nbDirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            return (sdDirs, nbDirs);
        }

        private static bool IsNarrowBandFolderName(string folderName)
        {
            // “협대역 폴더”가 특정 이름으로 관리되는 케이스가 많아서 우선 이 규칙
            if (string.Equals(folderName, "협대역", StringComparison.OrdinalIgnoreCase))
                return true;

            // 혹시 "협대역_원음" 같은 변형이 있으면 여기서 잡힘
            return folderName.Contains("협대역", StringComparison.OrdinalIgnoreCase);
        }

        // ========================= .sd 스캔 =========================

        private static ShortPeriodSdPackage ScanSdFolder(string sdDir)
        {
            var issues = new List<Issue>();

            var fileDict = BuildFileDict(sdDir);

            var wavs = fileDict
                .Where(kv => kv.Key.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (wavs.Count == 0)
                issues.Add(new Issue(IssueSeverity.Error, ".sd 폴더인데 .wav가 없음(요구사항 위반)."));

            fileDict.TryGetValue("SPSlist.txt", out var spsList);

            // optional이라고 했으니 Warning 정도로만
            if (spsList == null)
                issues.Add(new Issue(IssueSeverity.Warning, "SPSlist.txt 없음(있을 수도/없을 수도)."));

            return new ShortPeriodSdPackage(sdDir, wavs, spsList, issues);
        }

        // ========================= 협대역 스캔 =========================

        private static NarrowBandFolder ScanNarrowBandFolder(string nbDir)
        {
            var issues = new List<Issue>();
            var fileDict = BuildFileDict(nbDir);

            var wavs = fileDict
                .Where(kv => kv.Key.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (wavs.Count == 0)
            {
                issues.Add(new Issue(IssueSeverity.Error, "협대역 폴더인데 .wav가 없음(요구사항 위반)."));
                return new NarrowBandFolder(nbDir, Array.Empty<NarrowBandGroup>(), fileDict.Values.ToList(), issues);
            }

            var groups = new List<NarrowBandGroup>();
            var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var wavPath in wavs)
            {
                consumed.Add(wavPath);

                var baseName = Path.GetFileNameWithoutExtension(wavPath);
                var gIssues = new List<Issue>();

                string? Get(string fileName)
                {
                    if (fileDict.TryGetValue(fileName, out var p))
                    {
                        consumed.Add(p);
                        return p;
                    }
                    return null;
                }

                // 네가 정의한 “묶음” 그대로 (optional 매칭)
                var demonGrm = Get($"{baseName}_Demon.grm");

                var demonDraw =
                    Get($"{baseName}_Demondrawinglist.txt") ??
                    Get($"{baseName}_DemonDrawinglist.txt") ??
                    Get($"{baseName}_DemonDrawingList.txt");

                var detectDat = Get($"{baseName}_Detect.dat");
                var eventGrm = Get($"{baseName}_Event.grm");
                var lofarGrm = Get($"{baseName}_Lofar.grm");

                var lofarDraw =
                    Get($"{baseName}_LofarDrawinglist.txt") ??
                    Get($"{baseName}_LofarDrawinglist.txt") ??
                    Get($"{baseName}_LofarDrawingList.txt");

                var nbList = Get($"{baseName}_NBlist.txt");
                var spsList = Get($"{baseName}_SPSlist.txt");

                // 그룹 최소 1개 이상이라고 했으니 폴더 레벨에서만 보장되면 OK.
                // 그룹 자체는 wav만 있어도 성립.
                if (demonGrm == null && demonDraw == null && detectDat == null &&
                    eventGrm == null && lofarGrm == null && lofarDraw == null &&
                    nbList == null && spsList == null)
                {
                    gIssues.Add(new Issue(IssueSeverity.Info, "이 그룹은 wav만 있고 나머지 부가 파일이 없음(가능한 케이스)."));
                }

                // baseName_ 로 시작하는데 위에서 매칭되지 않은 파일들은 그룹 extra로 붙임
                var groupExtras = fileDict
                    .Where(kv =>
                        kv.Key.StartsWith(baseName + "_", StringComparison.OrdinalIgnoreCase) &&
                        !consumed.Contains(kv.Value))
                    .Select(kv => kv.Value)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var ex in groupExtras)
                    consumed.Add(ex);

                groups.Add(new NarrowBandGroup(
                    BaseName: baseName,
                    WavPath: wavPath,
                    DemonGrmPath: demonGrm,
                    DemonDrawingListPath: demonDraw,
                    DetectDatPath: detectDat,
                    EventGrmPath: eventGrm,
                    LofarGrmPath: lofarGrm,
                    LofarDrawingListPath: lofarDraw,
                    NbListPath: nbList,
                    SpsListPath: spsList,
                    ExtraFiles: groupExtras,
                    Issues: gIssues));
            }

            // 어떤 그룹에도 안 붙은 파일들
            var extraFiles = fileDict.Values
                .Where(p => !consumed.Contains(p))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // “최소 1개 이상 그룹” 조건: wav가 있으니 groups.Count >= 1 성립
            return new NarrowBandFolder(nbDir, groups, extraFiles, issues);
        }

        // ========================= 파일/디렉터리 유틸 =========================

        private static IEnumerable<string> SafeEnumerateDirectories(string path)
        {
            try { return Directory.EnumerateDirectories(path); }
            catch { return Array.Empty<string>(); }
        }

        private static Dictionary<string, string> BuildFileDict(string dir)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly); }
            catch { files = Array.Empty<string>(); }

            foreach (var p in files)
            {
                var name = Path.GetFileName(p);
                if (!dict.ContainsKey(name))
                    dict[name] = p;
            }
            return dict;
        }
    }
}
