using DBImplant_N1toN2.Features.ShortPeriod.Models;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using static DBImplant_N1toN2.Features.ShortPeriod.Parsers.ClaFileReader;
using static DBImplant_N1toN2.Features.ShortPeriod.Parsers.EventListReader;

namespace DBImplant_N1toN2.Features.ShortPeriod.Parsers
{
    public static partial class SdParser
    {
        public static SdPackageParseResult Parse(string sdDir, CancellationToken ct)
        {
            if (!Directory.Exists(sdDir))
                throw new DirectoryNotFoundException(sdDir);

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var res = new SdPackageParseResult
            {
                SdDir = sdDir,
                PreciseName = Path.GetFileName(sdDir.TrimEnd(Path.DirectorySeparatorChar))
            };

            ct.ThrowIfCancellationRequested();

            // 1) 루트 Signal_*.wav 리스트화 (byte[] 로 읽지 않음)
            FillFiles(res.RootSignalWavs, sdDir, "Signal_*.wav", SearchOption.TopDirectoryOnly);

            // 2) SPSlist.txt 파싱 (merge 금지: “그대로” 보관)
            ParseSps(sdDir, res.Sps);

            // 3) 이벤트리스트.lst 파싱
            ParseLst(sdDir, "이벤트리스트.lst", isTR: null, res.EventLst);

            // 4) 정밀분석설정.set 파싱
            ParseSet(sdDir, res.SetParam);

            // 5) 정밀분석/AI, TR 파싱
            var analysisDir = Path.Combine(sdDir, "정밀분석");
            ParseAnalysisFolder(Path.Combine(analysisDir, "AI"),
                lstName: "AI탐지리스트.lst",
                claName: "ai_식별결과.cla",
                tmpName: "ai_식별결과.tmp",
                isTrLst: false,
                isAi: true,
                res.Ai,
                ct);

            ParseAnalysisFolder(Path.Combine(analysisDir, "TR"),
                lstName: "순간소음탐지리스트.lst",
                claName: "tr_식별결과.cla",
                tmpName: "tr_식별결과.tmp",
                isTrLst: true,
                isAi: false,
                res.Tr,
                ct);

            return res;
        }

        private static void FillFiles(
            System.Collections.ObjectModel.ObservableCollection<FileRow> dst,
            string baseDir, string pattern, SearchOption opt)
        {
            dst.Clear();

            string[] files;
            try { files = Directory.GetFiles(baseDir, pattern, opt); }
            catch { files = Array.Empty<string>(); }

            foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var fi = new FileInfo(f);
                var rel = GetRelativePathSafe(baseDir, fi.FullName);
                dst.Add(new FileRow(rel, fi.FullName, fi.Length, fi.LastWriteTime));
            }
        }

        private static void ParseSps(string sdDir, SpsParseRow sps)
        {
            sps.Events.Clear();
            sps.Error = null;

            var path = Directory.GetFiles(sdDir, "SPSlist.txt", SearchOption.TopDirectoryOnly).FirstOrDefault();
            sps.FilePath = path;
            sps.Exists = path != null && File.Exists(path);

            if (!sps.Exists) return;

            try
            {
                var list = SpsListReader.ReadSpsList(path!);
                foreach (var ev in list) sps.Events.Add(ev);
            }
            catch (Exception ex)
            {
                sps.Error = ex.Message;
            }
        }

        private static void ParseLst(string sdDir, string lstName, bool? isTR, LstParseRow lst)
        {
            lst.Events.Clear();
            lst.Error = null;

            var path = Directory.GetFiles(sdDir, lstName, SearchOption.TopDirectoryOnly).FirstOrDefault();
            lst.FilePath = path;
            lst.Exists = path != null && File.Exists(path);

            if (!lst.Exists) return;

            try
            {
                var events = EventListReader.ReadEventListFile(path!, out var header, isTR);
                lst.Header = header;
                foreach (var ev in events) lst.Events.Add(ev);
            }
            catch (Exception ex)
            {
                lst.Error = ex.Message;
            }
        }

        private static void ParseSet(string sdDir, SetParamRow set)
        {
            set.Error = null;

            var path = Directory.GetFiles(sdDir, "정밀분석설정.set", SearchOption.TopDirectoryOnly).FirstOrDefault();
            set.FilePath = path;
            set.Exists = path != null && File.Exists(path);

            if (!set.Exists) return;

            try
            {
                // 기존 코드와 동일하게 949(EUC-KR) 기반으로 읽음
                var lines = File.ReadAllLines(path!, Encoding.GetEncoding(949))
                                .Select(x => x.Trim())
                                .Where(x => !string.IsNullOrEmpty(x))
                                .ToArray();

                set.DetectThreshold = lines.Length > 0 && double.TryParse(lines[0], out var detect) ? detect : 0;
                set.DtThreshold     = lines.Length > 1 && double.TryParse(lines[1], out var dt) ? dt : 0;
                set.BwThreshold     = lines.Length > 2 && double.TryParse(lines[2], out var bw) ? bw : 0;
            }
            catch (Exception ex)
            {
                set.Error = ex.Message;
            }
        }

    }
}
