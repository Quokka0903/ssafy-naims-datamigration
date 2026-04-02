using Simul_AcousticInfoAnalysis.BWFReader;
using Simul_AcousticInfoAnalysis.ReadFileToDB;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using static Simul_AcousticInfoAnalysis.Import.SdLoader;
using static Simul_AcousticInfoAnalysis.ReadFileToDB.ClaFileReader;
using static Simul_AcousticInfoAnalysis.ReadFileToDB.EventListReader;
using static Simul_AcousticInfoAnalysis.ReadFileToDB.TmpFileReader;

namespace Simul_AcousticInfoAnalysis.Import
{
    /*──────────────────────────── SdLoaded DTO ────────────────────────────*/
    public sealed class SdLoaded
    {
        public List<ShortPeriodEvent>? EventList { get; set; }
        public List<ShortPeriodEvent>? AiList { get; set; }
        public List<ShortPeriodEvent>? TrList { get; set; }
        public AIClassificationResult? AiCla { get; set; }
        public TRClassificationResult? TrCla { get; set; }
        public List<AI_EVENT_RESULT>? AiTmp { get; set; }
        public List<TR_EVENT_RESULT>? TrTmp { get; set; }

        public List<ShortPeriodEvent> SpsList { get; set; }
        public SsParam SsParam { get; set; }
    }

    public record SsParam(
        double DtThreshold, int DtThresholdStep,
        double BwThreshold, int BwThresholdStep,
        int Window, int KaiserBesselParameter,
        int Overlap, int Fft, double FreqResolution);

    /*──────────────────────────── SdLoader ───────────────────────────────*/
    public static class SdLoader
    {
        /*────────────────────── Event / Segment / Frame ───────────────────*/
        public class ShortPeriodEvent
        {
            public long EventID;
            public long StartTime;
            public long EndTime;
            public long EventLength;
            public string DetectingType;
            public long PRI;
            public string TRMMode;
            public double SPL;
            public string Modulation;
            public string Comment;
            public string GramSnippetFileName;
            public byte[] GramFile;
            public string AudioSnippetFileName;
            public byte[] AudioSnippetFile;
            public byte[] SigFile;
            public DateTime SigStartTime;
            public string SnippetComment;

            public int SegmentCount;
            public int AiSegmentCount;
            public int TrSegmentCount;
            public int UnkSegmentCount;
            public bool ManualOrAuto;
            public string DirPath;

            /* ─── AI 전용 ───*/
            public long lPID;
            public long lPulseLength;
            public long lPri;
            public string strTrmMode;
            public double fSpl;
            public string strPulseModulation;
            public int nSegmentNum;
            public string strSonarName;
            public string strPulseName;
            public double fScore;
            public string strGramSnippetFileName;
            public string strAudioSnippetFileName;
            public string strDetectionSeaAreaLatitude;
            public string strDetectionSeaAreaLongitude;
            public double fDistance;
            public int nGramSnippetNum;
            public int nAudioSnippetNum;
            public byte byteAppliedState;
            public int lDBApplyID;
            public string strDBAppliedDate;
            public byte byteSnippetAppliedState;

            public List<ShortPeriodSegment> Segments { get; set; } = new();
        }

        public class ShortPeriodSegment
        {
            public int SegmentID;
            public long StartTime;
            public long EndTime;
            public long Length;
            public double LDAx;
            public double LDAy;
            public int DetectingType;
            public int FrameCount;
            public double StartFreq;
            public double EndFreq;
            public int Modulation;
            public double CenterFreq;
            public double BandWidth;

            public string GramSnippetFileName;
            public byte[] GramFile;
            public string SnippetFileName;
            public byte[] SnippetFile;

            public long DBSegID;
            public double PatternValue;

            /* ─── AI 전용 ───*/
            public int lSID;
            public double fStartFreq;
            public double fEndFreq;
            public long lSegmentLength;
            public string strModulation;
            public double fScore;
            public byte byteAppliedState;
            public int lDBApplyID;

            /* ─── TR 전용 ───*/
            public string strGroup;
            public string strTransientName;
            public string strComments;
            public int nAudioSnippetNum;
            public string strDBAppliedDate;

            public List<ShortPeriodFrame> Frames { get; set; } = new();
        }

        public class ShortPeriodFrame
        {
            public long FrameID;
            public double Frequency;
            public List<double> MFBs { get; set; } = new();
            public float Score;
            public byte AppliedState;
            public int DBApplyID;
            public long DBSegID;
            public long DBFrmID;
            public byte SnippetAppliedState;
        }

        /// <summary>
        /// 지정된 sdDir 경로에서 분석용 파일과 디렉터리를 로드합니다.
        /// ─ 이벤트리스트, 정밀분석설정, SPS 리스트
        /// ─ 정밀분석/AI 및 TR 하위 구조 (Snippets, SnpTmp, .cla, .tmp, .lst)
        /// </summary>
        public static SdLoaded Load(string sdDir)
        {
            if (!Directory.Exists(sdDir))
                throw new DirectoryNotFoundException(sdDir);

            var loaded = new SdLoaded();

            // ──────────────────────────────────────────────────────────────────────────
            // 1) 최상위 목록파일(.lst) 및 정밀분석설정(.set)
            // ──────────────────────────────────────────────────────────────────────────
            string evtLst = Directory.GetFiles(sdDir, "이벤트리스트.lst").FirstOrDefault();
            string setFile = Directory.GetFiles(sdDir, "정밀분석설정.set").FirstOrDefault();
            if (evtLst != null)
            {
                Debug.WriteLine($"[PARSE] 이벤트리스트  {evtLst}");
                loaded.EventList = EventListReader.ReadEventListFile(evtLst, out var header);
                AttachSignalWavs(sdDir, loaded.EventList);
            }
            if (setFile != null)
            {
                Debug.WriteLine($"[PARSE] 정밀분석설정  {setFile}");
                if (File.Exists(setFile))
                {
                    string[] lines = File.ReadAllLines(setFile, Encoding.GetEncoding(949))   // EUC-KR
                                       .Select(l => l.Trim())
                                       .Where(l => !string.IsNullOrEmpty(l))
                                       .ToArray();

                    double bwThreshold = lines.Length > 0 && double.TryParse(lines[0], out var bw) ? bw : 0.0;
                    double dtThreshold = lines.Length > 1 && double.TryParse(lines[1], out var dt) ? dt : 0.0;

                    loaded.SsParam = new SsParam(
                        dtThreshold, 0,          // DtThreshold, DtThresholdStep
                        bwThreshold, 0,          // BwThreshold, BwThresholdStep
                        0, 0,                     // Window, KaiserBesselParameter
                        0, 0,                     // Overlap, N-FFT
                        0.0                       // FreqResolution
                    );

                    Debug.WriteLine($"[PARSE] .set → BW={bwThreshold}, DT={dtThreshold}");
                }
                else
                {
                    loaded.SsParam = null;   // 또는 기본값
                }
            }

            // ──────────────────────────────────────────────────────────────────────────
            // 2) SPS 리스트
            // ──────────────────────────────────────────────────────────────────────────
            string spsList = Directory.GetFiles(sdDir, "SPSlist.txt").FirstOrDefault();
            if (spsList != null)
            {
                Debug.WriteLine($"[PARSE] SPSlist.txt  {spsList}");
                loaded.SpsList = SpsListReader.ReadSpsList(spsList);
            }

            // 이벤트리스트와 SPS리스트가 모두 있으면 병합
            if (loaded.EventList != null && loaded.SpsList != null)
            {
                MergeSpsIntoEventList(loaded.EventList, loaded.SpsList);
            }

            // ──────────────────────────────────────────────────────────────────────────
            // 3) 정밀분석/AI 및 TR 디렉터리 처리
            // ──────────────────────────────────────────────────────────────────────────
            string analysisDir = Path.Combine(sdDir, "정밀분석");
            if (Directory.Exists(analysisDir))
            {
                // AI 처리
                string aiDir = Path.Combine(analysisDir, "AI");
                if (Directory.Exists(aiDir))
                {
                    /*// Snippets 폴더 경로
                    string aiSnippets = Path.Combine(aiDir, "Snippets");
                    loaded.AiSnippets = Directory.Exists(aiSnippets)
                        ? Directory.GetFiles(aiSnippets, "*.wav").ToList()
                        : new List<string>();

                    // SnpTmp 폴더 경로
                    string aiTmpFolder = Path.Combine(aiDir, "SnpTmp");
                    loaded.AiTmpFiles = Directory.Exists(aiTmpFolder)
                        ? Directory.GetFiles(aiTmpFolder, "*.*").ToList()
                        : new List<string>();*/

                    // AI 탐지리스트(.lst)
                    string aiLst = Directory.GetFiles(aiDir, "AI탐지리스트.lst").FirstOrDefault();
                    if (aiLst != null)
                    {
                        Debug.WriteLine($"[PARSE] AI탐지리스트  {aiLst}");
                        loaded.AiList = EventListReader.ReadEventListFile(aiLst, out var header);
                        AttachSegmentSnippets(Path.Combine(aiDir, "Snippets"), loaded.AiList);
                        AttachEventSnippets(Path.Combine(aiDir, "Snippets"), loaded.AiList);
                    }

                    // AI 분류결과 (.cla, .tmp)
                    string aiCla = Directory.GetFiles(aiDir, "ai_식별결과.cla").FirstOrDefault();
                    if (aiCla != null)
                    {
                        Debug.WriteLine($"[PARSE] AI 식별결과.cla  {aiCla}");
                        loaded.AiCla = ClaFileReader.ReadCCFAIClassificationResult(aiCla);
                    }
                    /*string aiTmp = Directory.GetFiles(aiDir, "ai_식별결과.tmp").FirstOrDefault();
                    if (aiTmp != null)
                    {
                        Debug.WriteLine($"[PARSE] AI 식별결과.tmp  {aiTmp}");
                        loaded.AiTmp = TmpFileReader.ReadAIResultListFile(aiTmp);
                    }*/
                }

                // TR 처리
                string trDir = Path.Combine(analysisDir, "TR");
                if (Directory.Exists(trDir))
                {
                    /*// Snippets 폴더 경로
                    string trSnippets = Path.Combine(trDir, "Snippets");
                    loaded.TrSnippets = Directory.Exists(trSnippets)
                        ? Directory.GetFiles(trSnippets, "*.wav").ToList()
                        : new List<string>();

                    // SnpTmp 폴더 경로
                    string trTmpFolder = Path.Combine(trDir, "SnpTmp");
                    loaded.TrTmpFiles = Directory.Exists(trTmpFolder)
                        ? Directory.GetFiles(trTmpFolder, "*.*").ToList()
                        : new List<string>();*/

                    // 순간소음탐지리스트(.lst)
                    string trLst = Directory.GetFiles(trDir, "순간소음탐지리스트.lst").FirstOrDefault();
                    if (trLst != null)
                    {
                        Debug.WriteLine($"[PARSE] 순간소음탐지리스트  {trLst}");
                        loaded.TrList = EventListReader.ReadEventListFile(trLst, out var header, true);
                        AttachSegmentSnippets(Path.Combine(trDir, "Snippets"), loaded.TrList);
                    }

                    // TR 분류결과 (.cla, .tmp)
                    string trCla = Directory.GetFiles(trDir, "tr_식별결과.cla").FirstOrDefault();
                    if (trCla != null)
                    {
                        Debug.WriteLine($"[PARSE] TR 식별결과.cla  {trCla}");
                        loaded.TrCla = ClaFileReader.ReadCCFTRClassificationResult(trCla);
                    }
                    /*string trTmp = Directory.GetFiles(trDir, "tr_식별결과.tmp").FirstOrDefault();
                    if (trTmp != null)
                    {
                        Debug.WriteLine($"[PARSE] TR 식별결과.tmp  {trTmp}");
                        loaded.TrTmp = TmpFileReader.ReadTRResultListFile(trTmp);
                    }*/
                }
            }

            return loaded;
        }


        // 이벤트 wav파일
        private static void AttachSignalWavs(string sdDir, List<ShortPeriodEvent>? events)
        {
            if (events == null || events.Count == 0) return;

            // ① Signal_*.wav 모두 수집 & 정렬
            var sigFiles = Directory
                .GetFiles(sdDir, "Signal_*.wav", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int n = Math.Min(sigFiles.Count, events.Count);

            // ② 등장순서대로 매핑
            for (int i = 0; i < n; i++)
            {
                var ev = events[i];
                var wavPath = sigFiles[i];

                //ev.AudioSnippetFileName = Path.GetFileName(wavPath);   // 파일명
                ev.SigFile = File.ReadAllBytes(wavPath);  // 바이트 배열
                
            }
        }


        /// <summary>
        /// AI 이벤트(ShortPeriodEvent)의 Gram/Wav 스니펫을 Snippets 폴더에서 찾아
        /// byte[] 필드(GramFile, AudioSnippetFile)에 채워 넣는다.
        /// </summary>
        /// <param name="snippetDir">“…\\정밀분석\\AI\\Snippets” 폴더</param>
        /// <param name="evList">AI 탐지리스트(.lst) 혹은 머지된 EventList</param>
        internal static void AttachEventSnippets(string snippetDir,
                                                 List<ShortPeriodEvent>? evList)
        {
            if (evList == null || !Directory.Exists(snippetDir)) return;

            var files = Directory.GetFiles(snippetDir, "*.*", SearchOption.TopDirectoryOnly);
            var dict = files.ToDictionary(Path.GetFileName,
                                           p => p,
                                           StringComparer.OrdinalIgnoreCase);

            int hitWav = 0, hitGrm = 0;

            foreach (var ev in evList)
            {
                // ─ Gram(그램 이미지)
                if (!string.IsNullOrWhiteSpace(ev.GramSnippetFileName) &&
                    ev.GramFile == null &&
                    dict.TryGetValue(ev.GramSnippetFileName.Trim(), out var gPath))
                {
                    ev.GramFile = File.ReadAllBytes(gPath);
                    hitGrm++;
                    Debug.WriteLine($"[EV-SNIP] GRM  {ev.GramSnippetFileName}");
                }
                else if (ev.GramFile == null && !string.IsNullOrWhiteSpace(ev.AudioSnippetFileName))
                {
                    // WAV 이름이 ai_40255901.wav 라면 ai_40255901.grm 을 찾아본다
                    string guess = Path.GetFileNameWithoutExtension(ev.AudioSnippetFileName) + ".grm";

                    if (dict.TryGetValue(guess, out var g2Path))
                    {
                        ev.GramFile = File.ReadAllBytes(g2Path);
                        ev.GramSnippetFileName = guess;          // 없던 메타도 채워둠
                        hitGrm++;
                        Debug.WriteLine($"[EV-SNIP] GRM*(inferred)  {guess}");
                    }
                }

                // ─ Audio wav
                if (!string.IsNullOrWhiteSpace(ev.AudioSnippetFileName) &&
                    ev.AudioSnippetFile == null &&
                    dict.TryGetValue(ev.AudioSnippetFileName.Trim(), out var wPath))
                {
                    ev.AudioSnippetFile = File.ReadAllBytes(wPath);
                    hitWav++;
                    Debug.WriteLine($"[EV-SNIP] WAV  {ev.AudioSnippetFileName}");
                }
            }

            Debug.WriteLine($"[EV-SNIP] attach completed  WAV:{hitWav}  GRM:{hitGrm}");
        }


        // 세그먼트 wav, grm 파일
        private static void AttachSegmentSnippets(
    string snippetDir,                       // "…\\정밀분석\\AI\\Snippets"  또는  "…\\정밀분석\\TR\\Snippets"
    List<ShortPeriodEvent>? eventList)
        {
            if (eventList == null || !Directory.Exists(snippetDir)) return;

            // ① 디렉터리에 있는 wav → Dictionary<파일명 , 전체경로>
            var wavDict = Directory
                .GetFiles(snippetDir, "*.wav", SearchOption.TopDirectoryOnly)
                .ToDictionary(Path.GetFileName, p => p, StringComparer.OrdinalIgnoreCase);

            var grmDict = Directory
                .GetFiles(snippetDir, "*.grm", SearchOption.TopDirectoryOnly)
                .ToDictionary(Path.GetFileName, p => p, StringComparer.OrdinalIgnoreCase);

            /* ② 모든 세그먼트 순회하며 매칭 */
            int hitWav = 0, hitGrm = 0;

            foreach (var ev in eventList)
            {
                foreach (var seg in ev.Segments)
                {
                    string key = seg.SnippetFileName?.Trim();   // ← 앞뒤 공백 제거
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    if (wavDict.TryGetValue(key, out var wavPath))
                    {
                        seg.SnippetFile = File.ReadAllBytes(wavPath);
                        hitWav++;
                        Debug.WriteLine($"[ATTACH] WAV  {key}");
                    }

                    if (grmDict.TryGetValue(key, out var grmPath))
                    {
                        seg.GramFile = File.ReadAllBytes(grmPath);
                        hitGrm++;
                        Debug.WriteLine($"[ATTACH] GRM  {key}");
                    }
                }
            }

            /* ─ 루프 종료 후 요약 ─ */
            Debug.WriteLine($"[ATTACH] Snippet attach completed  WAV:{hitWav}  GRM:{hitGrm}");
        }


        /// <summary>
        /// .lst 로 읽은 EventList 에 SPSlist 값을 채워 넣는다.
        /// ① 동일 Event 는 EventID(우선) → StartTime 으로 매칭  
        /// ② Event 안의 Segment 는 SegmentID(우선) → StartTime 으로 매칭  
        /// ③ EventList 에 없는 Event / Segment 는 새로 추가
        /// </summary>
        /// <summary>
        /// .lst 로 읽은 EventList 에 SPSlist 값을 병합한다.
        /// ① Event : EventID → StartTime 순으로 매칭
        /// ② Segment : SegmentID → StartTime 순으로 매칭
        /// ③ EventList에 없으면 그대로 추가
        /// (중복 Key 발생 시 TryAdd 로 무시)
        /// </summary>
        public static void MergeSpsIntoEventList(
            List<ShortPeriodEvent>? eventList,      // nullable 허용
            List<ShortPeriodEvent>? spsList)
        {
            if (spsList == null || spsList.Count == 0) return;

            // .lst 자체가 없었던 경우 → 그대로 복사
            if (eventList == null || eventList.Count == 0)
            {
                eventList?.Clear();
                eventList ??= new List<ShortPeriodEvent>();
                eventList.AddRange(spsList.Select(CloneEvent));
                return;
            }

            /*───── 1) Event 딕셔너리 구축 ─────*/
            var evById = new Dictionary<long, ShortPeriodEvent>();
            var evBySta = new Dictionary<long, ShortPeriodEvent>();

            foreach (var ev in eventList)
            {
                if (ev.EventID != 0) evById.TryAdd(ev.EventID, ev);
                if (ev.StartTime != 0) evBySta.TryAdd(ev.StartTime, ev);
            }

            /*───── 2) 병합 루프 ─────*/
            foreach (var spsEv in spsList)
            {
                // 동일 이벤트 찾기
                if (!evById.TryGetValue(spsEv.EventID, out var ev))
                    evBySta.TryGetValue(spsEv.StartTime, out ev);

                // 없으면 새로 추가
                if (ev == null)
                {
                    var cloned = CloneEvent(spsEv);
                    eventList.Add(cloned);
                    if (cloned.EventID != 0) evById.TryAdd(cloned.EventID, cloned);
                    if (cloned.StartTime != 0) evBySta.TryAdd(cloned.StartTime, cloned);
                    continue;
                }

                // 기존 이벤트 채우기
                FillIfEmpty(ev, spsEv);

                /*─ Segment 병합 ─*/
                var segById = new Dictionary<int, ShortPeriodSegment>();
                var segBySta = new Dictionary<long, ShortPeriodSegment>();

                foreach (var sg in ev.Segments)
                {
                    if (sg.SegmentID != 0) segById.TryAdd(sg.SegmentID, sg);
                    if (sg.StartTime != 0) segBySta.TryAdd(sg.StartTime, sg);
                }

                foreach (var spsSeg in spsEv.Segments)
                {
                    if (!segById.TryGetValue(spsSeg.SegmentID, out var seg))
                        segBySta.TryGetValue(spsSeg.StartTime, out seg);

                    if (seg == null)
                    {
                        var clonedSeg = CloneSegment(spsSeg);
                        ev.Segments.Add(clonedSeg);
                        if (clonedSeg.SegmentID != 0) segById.TryAdd(clonedSeg.SegmentID, clonedSeg);
                        if (clonedSeg.StartTime != 0) segBySta.TryAdd(clonedSeg.StartTime, clonedSeg);
                        continue;
                    }
                    FillIfEmpty(seg, spsSeg);
                }
            }
        }

        /*──────────────────────── Copy / Fill 헬퍼 ─────────────────────*/
        // ── SdLoader 내부 (Copy / Fill 헬퍼 묶음 자리) ──
        public static void CopyIfZero(ref long tgt, long src) { if (tgt == 0) tgt = src; }
        public static void CopyIfZero(ref int tgt, int src) { if (tgt == 0) tgt = src; }
        public static void CopyIfZero(ref double tgt, double src) { if (Math.Abs(tgt) < 1e-6) tgt = src; }
        public static void CopyIfZero(ref byte tgt, byte src) { if (tgt == 0) tgt = src; }   // ← 추가

        public static void CopyIfEmpty(ref string tgt, string src)
        { if (string.IsNullOrWhiteSpace(tgt)) tgt = src; }

        /*──────────────────────── FillIfEmpty(Event) ──────────────────*/
        private static void FillIfEmpty(ShortPeriodEvent tgt, ShortPeriodEvent src)
        {
            /* 공통 */
            CopyIfZero(ref tgt.EventLength, src.EventLength);
            CopyIfZero(ref tgt.PRI, src.PRI);
            CopyIfZero(ref tgt.SPL, src.SPL);
            CopyIfEmpty(ref tgt.DetectingType, src.DetectingType);
            CopyIfEmpty(ref tgt.TRMMode, src.TRMMode);
            CopyIfEmpty(ref tgt.Modulation, src.Modulation);
            CopyIfEmpty(ref tgt.Comment, src.Comment);
            CopyIfEmpty(ref tgt.GramSnippetFileName, src.GramSnippetFileName);
            CopyIfEmpty(ref tgt.AudioSnippetFileName, src.AudioSnippetFileName);
            CopyIfEmpty(ref tgt.SnippetComment, src.SnippetComment);

            /* AI 전용 */
            CopyIfZero(ref tgt.lPulseLength, src.lPulseLength);
            CopyIfZero(ref tgt.lPri, src.lPri);
            CopyIfEmpty(ref tgt.strTrmMode, src.strTrmMode);
            CopyIfZero(ref tgt.fSpl, src.fSpl);
            CopyIfEmpty(ref tgt.strPulseModulation, src.strPulseModulation);
            CopyIfZero(ref tgt.nSegmentNum, src.nSegmentNum);
            CopyIfEmpty(ref tgt.strSonarName, src.strSonarName);
            CopyIfEmpty(ref tgt.strPulseName, src.strPulseName);
            CopyIfZero(ref tgt.fScore, src.fScore);
            CopyIfEmpty(ref tgt.strDetectionSeaAreaLatitude, src.strDetectionSeaAreaLatitude);
            CopyIfEmpty(ref tgt.strDetectionSeaAreaLongitude, src.strDetectionSeaAreaLongitude);
            CopyIfZero(ref tgt.fDistance, src.fDistance);
            CopyIfZero(ref tgt.nGramSnippetNum, src.nGramSnippetNum);
            CopyIfZero(ref tgt.nAudioSnippetNum, src.nAudioSnippetNum);
            CopyIfZero(ref tgt.byteAppliedState, src.byteAppliedState);
            CopyIfZero(ref tgt.lDBApplyID, src.lDBApplyID);
            CopyIfEmpty(ref tgt.strDBAppliedDate, src.strDBAppliedDate);
            CopyIfZero(ref tgt.byteSnippetAppliedState, src.byteSnippetAppliedState);

            /*── 바이너리 필드 ─*/
            if (tgt.GramFile == null && src.GramFile != null)
                tgt.GramFile = src.GramFile;
            if (tgt.AudioSnippetFile == null && src.AudioSnippetFile != null)
                tgt.AudioSnippetFile = src.AudioSnippetFile;
            if (tgt.SigFile == null && src.SigFile != null)
                tgt.SigFile = src.SigFile;

            /* 카운트 */
            if (tgt.SegmentCount == 0) tgt.SegmentCount = src.SegmentCount;
            if (tgt.AiSegmentCount == 0) tgt.AiSegmentCount = src.AiSegmentCount;
            if (tgt.TrSegmentCount == 0) tgt.TrSegmentCount = src.TrSegmentCount;
            if (tgt.UnkSegmentCount == 0) tgt.UnkSegmentCount = src.UnkSegmentCount;
        }

        /*──────────────────────── FillIfEmpty(Segment) ─────────────────*/
        private static void FillIfEmpty(ShortPeriodSegment tgt, ShortPeriodSegment src)
        {
            /* 공통 */
            if (tgt.Length == 0) tgt.Length = src.Length;
            if (tgt.FrameCount == 0) tgt.FrameCount = src.FrameCount;
            if (Math.Abs(tgt.LDAx) < 1e-6) tgt.LDAx = src.LDAx;
            if (Math.Abs(tgt.LDAy) < 1e-6) tgt.LDAy = src.LDAy;
            if (Math.Abs(tgt.StartFreq) < 1e-6) tgt.StartFreq = src.StartFreq;
            if (Math.Abs(tgt.EndFreq) < 1e-6) tgt.EndFreq = src.EndFreq;
            if (Math.Abs(tgt.CenterFreq) < 1e-6) tgt.CenterFreq = src.CenterFreq;
            if (Math.Abs(tgt.BandWidth) < 1e-6) tgt.BandWidth = src.BandWidth;
            if (tgt.DetectingType == 0) tgt.DetectingType = src.DetectingType;

            CopyIfEmpty(ref tgt.SnippetFileName, src.SnippetFileName);
            if (tgt.SnippetFile == null && src.SnippetFile != null) tgt.SnippetFile = src.SnippetFile;
            if (tgt.GramFile == null && src.GramFile != null) tgt.GramFile = src.GramFile;

            if (tgt.Modulation == 0) tgt.Modulation = src.Modulation;
            if (tgt.DBSegID == 0) tgt.DBSegID = src.DBSegID;

            /* AI */
            CopyIfEmpty(ref tgt.strModulation, src.strModulation);
            CopyIfZero(ref tgt.fScore, src.fScore);
            CopyIfZero(ref tgt.byteAppliedState, src.byteAppliedState);
            CopyIfZero(ref tgt.lDBApplyID, src.lDBApplyID);
            if (tgt.lSegmentLength == 0) tgt.lSegmentLength = src.lSegmentLength;

            /* TR */
            CopyIfEmpty(ref tgt.strGroup, src.strGroup);
            CopyIfEmpty(ref tgt.strTransientName, src.strTransientName);
            CopyIfEmpty(ref tgt.strComments, src.strComments);
            CopyIfEmpty(ref tgt.strDBAppliedDate, src.strDBAppliedDate);
            CopyIfZero(ref tgt.nAudioSnippetNum, src.nAudioSnippetNum);
        }

        /*──────────────────────── Clone helpers ───────────────────────*/
        public static ShortPeriodSegment CloneSegment(ShortPeriodSegment src)
        {
            var sg = new ShortPeriodSegment();
            FillIfEmpty(sg, src);

            sg.SegmentID = src.SegmentID;
            sg.StartTime = src.StartTime;
            sg.EndTime = src.EndTime;
            sg.SnippetFile = src.SnippetFile;
            sg.SnippetFileName = src.SnippetFileName;
            sg.GramFile = src.GramFile;

            /* AI 필드 */
            sg.lSID = src.lSID;
            sg.fStartFreq = src.fStartFreq;
            sg.fEndFreq = src.fEndFreq;
            sg.lSegmentLength = src.lSegmentLength;
            sg.strModulation = src.strModulation;
            sg.fScore = src.fScore;
            sg.byteAppliedState = src.byteAppliedState;
            sg.lDBApplyID = src.lDBApplyID;

            /* TR 필드 */
            sg.strGroup = src.strGroup;
            sg.strTransientName = src.strTransientName;
            sg.strComments = src.strComments;
            sg.nAudioSnippetNum = src.nAudioSnippetNum;
            sg.strDBAppliedDate = src.strDBAppliedDate;

            sg.Frames = src.Frames;    // 얕은 복사
            return sg;
        }

        public static ShortPeriodEvent CloneEvent(ShortPeriodEvent src)
        {
            var ev = new ShortPeriodEvent();
            FillIfEmpty(ev, src);

            ev.EventID = src.EventID;
            ev.StartTime = src.StartTime;
            ev.EndTime = src.EndTime;
            ev.DirPath = src.DirPath;
            ev.ManualOrAuto = src.ManualOrAuto;

            ev.SigFile = src.SigFile;
            ev.GramFile = src.GramFile;
            ev.AudioSnippetFile = src.AudioSnippetFile;

            /* AI 필드 전부 */
            ev.lPID = src.lPID;
            ev.lPulseLength = src.lPulseLength;
            ev.lPri = src.lPri;
            ev.strTrmMode = src.strTrmMode;
            ev.fSpl = src.fSpl;
            ev.strPulseModulation = src.strPulseModulation;
            ev.nSegmentNum = src.nSegmentNum;
            ev.strSonarName = src.strSonarName;
            ev.strPulseName = src.strPulseName;
            ev.fScore = src.fScore;
            ev.strGramSnippetFileName = src.strGramSnippetFileName;
            ev.strAudioSnippetFileName = src.strAudioSnippetFileName;
            ev.strDetectionSeaAreaLatitude = src.strDetectionSeaAreaLatitude;
            ev.strDetectionSeaAreaLongitude = src.strDetectionSeaAreaLongitude;
            ev.fDistance = src.fDistance;
            ev.nGramSnippetNum = src.nGramSnippetNum;
            ev.nAudioSnippetNum = src.nAudioSnippetNum;
            ev.byteAppliedState = src.byteAppliedState;
            ev.lDBApplyID = src.lDBApplyID;
            ev.strDBAppliedDate = src.strDBAppliedDate;
            ev.byteSnippetAppliedState = src.byteSnippetAppliedState;

            ev.Segments = src.Segments.Select(CloneSegment).ToList();
            return ev;
        }
    }

    /*─────────────────────── Integrator (AI + TR) ───────────────────────*/
    public static class SdImportIntegrator
    {
        /*────────────────── public entry ──────────────────*/
        public static List<ShortPeriodEvent> BuildImportDbList(SdLoaded loaded)
        {
            var evList = loaded.EventList ?? new List<ShortPeriodEvent>();

            MergeEventList(evList, loaded.AiList);
            MergeEventList(evList, loaded.TrList);

            MergeAiCla(evList, loaded.AiCla);
            MergeTrCla(evList, loaded.TrCla);

            return evList;
        }

        private static void MergeEventList(List<ShortPeriodEvent> baseList,
                                           List<ShortPeriodEvent>? patch)
        {
            if (patch == null || patch.Count == 0) return;
            SdLoader.MergeSpsIntoEventList(baseList, patch);
        }

        /*────────────────── AI .cla 머지 ──────────────────*/
        private static void MergeAiCla(List<ShortPeriodEvent> evList,
                                       AIClassificationResult? aiCla)
        {
            if (aiCla == null) return;

            var evById = evList.Where(e => e.EventID != 0)
                               .ToDictionary(e => e.EventID);

            foreach (var pulse in aiCla.listPulseResults)
            {
                var p = pulse.aiPulseResult;

                long eid = p.infoPulse.lPID;
                if (!evById.TryGetValue(eid, out var ev))
                {
                    ev = new ShortPeriodEvent { EventID = eid };
                    evList.Add(ev); evById[eid] = ev;
                }

                /* Event ↔ Pulse */
                SdLoader.CopyIfEmpty(ref ev.strSonarName, p.strSonarName);
                SdLoader.CopyIfEmpty(ref ev.strPulseName, p.strPulseName);
                SdLoader.CopyIfZero(ref ev.fScore, p.fScore);
                SdLoader.CopyIfZero(ref ev.lPulseLength, p.infoPulse.lPulseLength);
                SdLoader.CopyIfZero(ref ev.lPri, p.infoPulse.lPri);
                SdLoader.CopyIfEmpty(ref ev.strTrmMode, p.infoPulse.strTrmMode);
                SdLoader.CopyIfZero(ref ev.fSpl, p.infoPulse.fSpl);
                SdLoader.CopyIfEmpty(ref ev.strPulseModulation, p.infoPulse.strPulseModulation);
                SdLoader.CopyIfZero(ref ev.nSegmentNum, p.infoPulse.nSegmentNum);

                SdLoader.CopyIfEmpty(ref ev.strGramSnippetFileName, p.strGramSnippetFileName);
                SdLoader.CopyIfEmpty(ref ev.strAudioSnippetFileName, p.strAudioSnippetFileName);
                SdLoader.CopyIfEmpty(ref ev.Comment, p.strComment);

                SdLoader.CopyIfEmpty(ref ev.strDetectionSeaAreaLatitude, p.strDetectionSeaAreaLatitude);
                SdLoader.CopyIfEmpty(ref ev.strDetectionSeaAreaLongitude, p.strDetectionSeaAreaLongitude);
                SdLoader.CopyIfZero(ref ev.fDistance, p.fDistance);

                SdLoader.CopyIfZero(ref ev.nGramSnippetNum, p.nGramSnippetNum);
                SdLoader.CopyIfZero(ref ev.nAudioSnippetNum, p.nAudioSnippetNum);
                SdLoader.CopyIfZero(ref ev.byteAppliedState, p.byteAppliedState);
                SdLoader.CopyIfZero(ref ev.lDBApplyID, p.lDBApplyID);
                SdLoader.CopyIfEmpty(ref ev.strDBAppliedDate, p.strDBAppliedDate);
                SdLoader.CopyIfZero(ref ev.byteSnippetAppliedState, p.byteSnippetAppliedState);

                /* Segment loop */
                var segById = ev.Segments.ToDictionary(s => s.SegmentID);
                foreach (var segR in pulse.listSegmentResults)
                {
                    if (!segById.TryGetValue(segR.lSID, out var seg))
                    {
                        seg = new ShortPeriodSegment { SegmentID = segR.lSID };
                        ev.Segments.Add(seg); segById[segR.lSID] = seg;
                    }

                    SdLoader.CopyIfZero(ref seg.lSegmentLength, segR.lSegmentLength);
                    SdLoader.CopyIfZero(ref seg.fStartFreq, segR.fStartFreq);
                    SdLoader.CopyIfZero(ref seg.fEndFreq, segR.fEndFreq);
                    SdLoader.CopyIfEmpty(ref seg.strModulation, segR.strModulation);
                    SdLoader.CopyIfZero(ref seg.fScore, segR.fScore);
                    SdLoader.CopyIfZero(ref seg.byteAppliedState, segR.byteAppliedState);
                    SdLoader.CopyIfZero(ref seg.lDBApplyID, segR.lDBApplyID);

                    /* 공통 필드 보강 */
                    if (seg.Length == 0) seg.Length = segR.lSegmentLength;
                    if (seg.StartFreq == 0) seg.StartFreq = segR.fStartFreq;
                    if (seg.EndFreq == 0) seg.EndFreq = segR.fEndFreq;
                    if (seg.ScoreNeedsUpdate(segR.fScore)) seg.PatternValue = segR.fScore;
                }
            }
        }

        /*────────────────── TR .cla 머지 (변경분 포함) ─────────────────*/
        private static void MergeTrCla(List<ShortPeriodEvent> evList,
                                       TRClassificationResult? trCla)
        {
            if (trCla == null) return;

            var evById = evList.Where(e => e.EventID != 0)
                               .ToDictionary(e => e.EventID);

            foreach (var segR in trCla.listTrResult)
            {
                if (!evById.TryGetValue(segR.lEventID, out var ev))
                {
                    ev = new ShortPeriodEvent { EventID = segR.lEventID };
                    evList.Add(ev); evById[segR.lEventID] = ev;
                }

                var seg = ev.Segments.FirstOrDefault(s => s.SegmentID == segR.lSegmentID);
                if (seg == null)
                {
                    seg = new ShortPeriodSegment { SegmentID = (int)segR.lSegmentID };
                    ev.Segments.Add(seg);
                }

                /* TR 전용 */
                SdLoader.CopyIfEmpty(ref seg.strGroup, segR.strGroup);
                SdLoader.CopyIfEmpty(ref seg.strTransientName, segR.strTransientName);
                SdLoader.CopyIfEmpty(ref seg.strComments, segR.strComments);
                SdLoader.CopyIfEmpty(ref seg.strDBAppliedDate, segR.strDBAppliedDate);
                SdLoader.CopyIfZero(ref seg.nAudioSnippetNum, segR.nAudioSnippetNum);

                /* 공통 */
                if (string.IsNullOrWhiteSpace(seg.SnippetFileName))
                    seg.SnippetFileName = segR.strAudioSnippetFileName;
                if (seg.Length == 0) seg.Length = segR.nFrameNum * 15625;
                seg.PatternValue = segR.fScore;
                if (seg.FrameCount == 0) seg.FrameCount = segR.nFrameNum;

                /* Frame list */
                if (seg.Frames.Count == 0 && segR.listFrameResults.Count > 0)
                {
                    foreach (var frR in segR.listFrameResults)
                    {
                        seg.Frames.Add(new ShortPeriodFrame
                        {
                            MFBs = frR.arrMFB,
                            AppliedState = frR.byteAppliedState,
                            DBApplyID = frR.lDBApplyID,
                            SnippetAppliedState = frR.byteSnippetAppliedState
                        });
                    }
                }
            }
        }
    }

    /*────────────────────── Segment Extension ─────────────────────*/
    internal static class SegmentExt
    {
        internal static bool ScoreNeedsUpdate(this ShortPeriodSegment seg, double newScore) =>
            Math.Abs(seg.PatternValue) < 1e-6 && Math.Abs(newScore) > 1e-6;
    }
}