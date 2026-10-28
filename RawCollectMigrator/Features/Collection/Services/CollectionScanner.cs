using System.IO;

namespace RawCollectMigrator.Features.Collection
{
    public static class CollectionScanner
    {
        /// <summary>
        /// 수집원음자료 루트(예: \\.../MSU/수집원음자료)에서 임무폴더 단위로 스캔.
        /// 각 임무폴더 내:
        ///   - 보조정보.alg → RAW_COLLECT_SUPPLEMENT
        ///   - 협대역/Signal_*.wav → RAW_SCHANNEL_INFO 1건씩
        /// </summary>
        public static List<CollectionRow> Scan(string collectRawRoot)
        {
            var rows = new List<CollectionRow>();
            if (!Directory.Exists(collectRawRoot)) return rows;

            foreach (var missionDir in Directory.EnumerateDirectories(collectRawRoot))
            {
                try
                {
                    rows.Add(ScanOneMission(missionDir));
                }
                catch
                {
                    // 권한 오류 등 무시하고 다음 폴더
                }
            }

            return rows.OrderBy(r => r.MissionName).ToList();
        }

        private static CollectionRow ScanOneMission(string missionDir)
        {
            var missionName = Path.GetFileName(missionDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            // 보조정보.alg
            var algPath = Path.Combine(missionDir, "보조정보.alg");
            var hasAlg = File.Exists(algPath);

            // [NAIMS-ISSUE:NB-FOLDER-CONVENTION] 협대역 폴더명·WAV 패턴 고정(다르면 0건)
            // 협대역/Signal_*.wav
            var nbDir = Path.Combine(missionDir, "협대역");
            var hasNb = Directory.Exists(nbDir);

            var wavFiles = Array.Empty<string>();
            if (hasNb)
            {
                try
                {
                    wavFiles = Directory.EnumerateFiles(nbDir, "Signal_*.wav", SearchOption.TopDirectoryOnly)
                        .OrderBy(f => f)
                        .ToArray();
                }
                catch { wavFiles = Array.Empty<string>(); }
            }

            // 폴더 총 크기 (TopDirectory만 — 깊은 탐색은 느릴 수 있음. 임무폴더 하위 전체 합산)
            long totalSize = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(missionDir, "*", SearchOption.AllDirectories))
                {
                    try { totalSize += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }

            DateTime lastWrite = DateTime.MinValue;
            try { lastWrite = Directory.GetLastWriteTime(missionDir); } catch { }

            var row = new CollectionRow
            {
                MissionFolderPath = missionDir,
                MissionName = missionName,
                HasSupplementAlg = hasAlg,
                SupplementAlgPath = hasAlg ? algPath : null,
                WavCount = wavFiles.Length,
                WavFiles = wavFiles,
                HasNbFolder = hasNb,
                TotalSizeBytes = totalSize,
                LastWriteTime = lastWrite
            };
            // 초기 기본 체크: WAV가 있는 폴더만 대상으로 (없으면 setter가 강제 false)
            row.IsSelected = row.CanBeSelected;
            if (!row.CanBeSelected)
                row.Status = "대상 없음";
            return row;
        }
    }
}
