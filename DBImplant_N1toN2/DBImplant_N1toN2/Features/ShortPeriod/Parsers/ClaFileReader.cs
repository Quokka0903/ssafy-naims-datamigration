using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Text;
using static DBImplant_N1toN2.Features.ShortPeriod.Parsers.EventListReader;

namespace DBImplant_N1toN2.Features.ShortPeriod.Parsers
{
    public class ClaFileReader
    {
        // 탐지 해역(위도·경도·거리) 정보를 담는 클래스
        public class DetectArea
        {
            public string strDetectionSeaAreaLatitude { get; set; }
            public string strDetectionSeaAreaLongitude { get; set; }
            public double fDistance { get; set; }
        }

        // TR 세그먼트 결과용
        public class TRSegmentResult
        {
            public long lEventID { get; set; }
            public long lSegmentID { get; set; }
            public string strGroup { get; set; }
            public string strTransientName { get; set; }
            public int nFrameNum { get; set; }
            public double fScore { get; set; }

            public string strComments { get; set; }
            public string strAudioSnippetFileName { get; set; }
            public int nAudioSnippetNum { get; set; }
            public byte byteAppliedState { get; set; }
            public int lDBApplyID { get; set; }
            public string strDBAppliedDate { get; set; }
            public byte byteSnippetAppliedState { get; set; }

            public List<TRFrameResult> listFrameResults { get; } = new List<TRFrameResult>();
        }

        // 한 프레임의 MFB 배열과 DB/스니펫 적용 상태를 담는 클래스
        public class TRFrameResult
        {
            // C++ arrMFB[i] 에 대응
            public List<double> arrMFB { get; set; } = new List<double>();

            // C++ pTRFrameResult->byteAppliedState
            public byte byteAppliedState { get; set; }

            // C++ pTRFrameResult->lDBApplyID
            public int lDBApplyID { get; set; }

            // C++ pTRFrameResult->byteSnippetAppliedSate
            public byte byteSnippetAppliedState { get; set; }
        }

        // TRClassificationResult 에 DetectArea 추가
        public class TRClassificationResult
        {
            public Header header = new Header();
            public DetectArea infoDetectArea = new DetectArea();
            public List<TRSegmentResult> listTrResult = new List<TRSegmentResult>();
        }

        // AI 펄스 정보 중 Pulse 고유 필드
        public class PulseDetail
        {
            public long lPID { get; set; }
            public long lPulseLength { get; set; }
            public long lPri { get; set; }
            public string strTrmMode { get; set; }
            public double fSpl { get; set; }
            public string strPulseModulation { get; set; }
            public int nSegmentNum { get; set; }
        }

        // AIPulseResult 전체
        public class AIPulseResult
        {
            public Header header;             // (필요 없으면 삭제)
            public PulseInfo aiPulseResult = new PulseInfo();
            public List<AISegmentResult> listSegmentResults = new List<AISegmentResult>();
        }

        // PulseInfo 에서 쓰는 중간 구조
        public class PulseInfo
        {
            public PulseDetail infoPulse = new PulseDetail();

            public string strSonarName { get; set; }
            public string strPulseName { get; set; }
            public double fScore { get; set; }

            public string strGramSnippetFileName { get; set; }
            public string strAudioSnippetFileName { get; set; }
            public string strComment { get; set; }

            public string strDetectionSeaAreaLatitude { get; set; }
            public string strDetectionSeaAreaLongitude { get; set; }
            public double fDistance { get; set; }

            public int nGramSnippetNum { get; set; }
            public int nAudioSnippetNum { get; set; }
            public byte byteAppliedState { get; set; }
            public int lDBApplyID { get; set; }
            public string strDBAppliedDate { get; set; }
            public byte byteSnippetAppliedState { get; set; }
        }

        // AI 세그먼트 결과용
        public class AISegmentResult
        {
            public int lSID { get; set; }
            public double fStartFreq { get; set; }
            public double fEndFreq { get; set; }
            public long lSegmentLength { get; set; }
            public string strModulation { get; set; }
            public double fScore { get; set; }
            public byte byteAppliedState { get; set; }
            public int lDBApplyID { get; set; }
        }

        // AIClassificationResult 에도 DetectArea 추가 (펄스마다 재사용)
        public class AIClassificationResult
        {
            public Header header = new Header();
            public List<AIPulseResult> listPulseResults = new List<AIPulseResult>();
        }


        // ── Helper methods ──
        private static void SkipNewline(FileStream fs)
        {
            // CRLF 제거
            if (fs.ReadByte() != '\r') fs.Position--;
            if (fs.ReadByte() != '\n') fs.Position--;
        }

        private static string Decode(Encoding enc, byte[] buf, int offset, int count)
            => enc.GetString(buf, offset, count).Trim();

        private static long TryParseLong(string s)
            => long.TryParse(s.Trim(), out var v) ? v : 0;

        private static int TryParseInt(string s)
            => int.TryParse(s.Trim(), out var v) ? v : 0;

        private static byte TryParseByte(string s)
            => byte.TryParse(s.Trim(), out var v) ? v : (byte)0;

        // ── TRClassificationResult Reader ──
        public static TRClassificationResult ReadCCFTRClassificationResult(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            var enc = Encoding.GetEncoding(949);
            var result = new TRClassificationResult();

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);

            // 1) Header: "%80s%30s%30s%30s%20s%8s%8s\n"
            byte[] bufHeader = new byte[80 + 30 + 30 + 30 + 20 + 8 + 8];
            fs.Read(bufHeader, 0, bufHeader.Length);
            SkipNewline(fs);
            result.header.RawAcousticDataFileName = Decode(enc, bufHeader, 0, 80);
            result.header.DetectSystem = Decode(enc, bufHeader, 80, 30);
            result.header.DetectShipName = Decode(enc, bufHeader, 110, 30);
            result.header.DetectSonar = Decode(enc, bufHeader, 140, 30);
            result.header.DetectDate = Decode(enc, bufHeader, 170, 20);
            result.header.AnalyzerID = Decode(enc, bufHeader, 190, 8);
            result.header.ClassificationEquipment = Decode(enc, bufHeader, 198, 8);

            // 2) Count: "%8d\n"
            byte[] bufCount = new byte[8];
            fs.Read(bufCount, 0, 8); SkipNewline(fs);
            int nSegCount = TryParseInt(enc.GetString(bufCount));

            // 3) Detect Area: "%20s%20s%16.7f\n"
            byte[] bufArea = new byte[20 + 20 + 16];
            fs.Read(bufArea, 0, bufArea.Length); SkipNewline(fs);
            result.infoDetectArea.strDetectionSeaAreaLatitude = Decode(enc, bufArea, 0, 20);
            result.infoDetectArea.strDetectionSeaAreaLongitude = Decode(enc, bufArea, 20, 20);
            result.infoDetectArea.fDistance =
                double.TryParse(Decode(enc, bufArea, 40, 16), out var d) ? d : 0.0;

            // 4) Segments
            for (int i = 0; i < nSegCount; i++)
            {
                // 한 세그먼트: 두 번 fprintf → 284바이트
                // 첫 번째: "%8d%8d%20s%30s%8d%16.7f"   = 8+8+20+30+8+16 = 90
                // 두 번째: "%80s%80s%2d%2d%8d%20s%2d\n" = 80+80+2+2+8+20+2 = 194
                byte[] segBuf = new byte[284];
                fs.Read(segBuf, 0, segBuf.Length);
                SkipNewline(fs);

                var seg = new TRSegmentResult
                {
                    // 첫 번째 fprintf: "%8d%8d%20s%30s%8d%16.7f"
                    lEventID = TryParseLong(Decode(enc, segBuf, 0, 8)),
                    lSegmentID = TryParseLong(Decode(enc, segBuf, 8, 8)),
                    strGroup = Decode(enc, segBuf, 16, 20),
                    strTransientName = Decode(enc, segBuf, 36, 30),
                    nFrameNum = TryParseInt(Decode(enc, segBuf, 66, 8)),
                    fScore = double.TryParse(Decode(enc, segBuf, 74, 16), out var sc) ? sc : 0.0,

                    // 두 번째 fprintf: "%80s%80s%2d%2d%8d%20s%2d"
                    strComments = Decode(enc, segBuf, 90, 80),
                    strAudioSnippetFileName = Decode(enc, segBuf, 170, 80),
                    nAudioSnippetNum = TryParseInt(Decode(enc, segBuf, 250, 2)),
                    byteAppliedState = TryParseByte(Decode(enc, segBuf, 252, 2)),
                    lDBApplyID = TryParseInt(Decode(enc, segBuf, 254, 8)),
                    strDBAppliedDate = Decode(enc, segBuf, 262, 20),
                    byteSnippetAppliedState = TryParseByte(Decode(enc, segBuf, 282, 2))
                };

                // 5) Frame 정보: 먼저 MFB count "%2d\n"
                byte[] bufMfbCount = new byte[2];
                fs.Read(bufMfbCount, 0, 2); SkipNewline(fs);
                int mfbCount = TryParseInt(enc.GetString(bufMfbCount));

                for (int j = 0; j < seg.nFrameNum; j++)
                {
                    // 프레임: mfbCount*9 + 2+10+2 = mfbCount*9 +14 bytes
                    int frameLen = mfbCount * 9 + 14;
                    byte[] frameBuf = new byte[frameLen];
                    fs.Read(frameBuf, 0, frameLen);
                    SkipNewline(fs);

                    var rawLine = enc.GetString(frameBuf);
                    var fr = new TRFrameResult();
                    // MFB 배열
                    for (int k = 0; k < mfbCount; k++)
                    {
                        int pos = k * 9;
                        string cell = rawLine.Substring(pos, 9).Trim();
                        fr.arrMFB.Add(double.TryParse(cell, out var v) ? v : 0.0);
                    }
                    // DB/스니펫 상태
                    fr.byteAppliedState = TryParseByte(rawLine.Substring(mfbCount * 9, 2));
                    fr.lDBApplyID = TryParseInt(rawLine.Substring(mfbCount * 9 + 2, 10));
                    fr.byteSnippetAppliedState = TryParseByte(rawLine.Substring(mfbCount * 9 + 12, 2));

                    seg.listFrameResults.Add(fr);
                }

                result.listTrResult.Add(seg);
            }

            return result;
        }

        // ── AIClassificationResult Reader ──
        public static AIClassificationResult ReadCCFAIClassificationResult(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            var enc = Encoding.GetEncoding(949);
            var result = new AIClassificationResult();

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);

            // Header: "%80s%30s%30s%30s%20s%8s%8s\n"
            byte[] bufH = new byte[80 + 30 + 30 + 30 + 20 + 8 + 8];
            fs.Read(bufH, 0, bufH.Length); SkipNewline(fs);
            result.header.RawAcousticDataFileName = Decode(enc, bufH, 0, 80);
            result.header.DetectSystem = Decode(enc, bufH, 80, 30);
            result.header.DetectShipName = Decode(enc, bufH, 110, 30);
            result.header.DetectSonar = Decode(enc, bufH, 140, 30);
            result.header.DetectDate = Decode(enc, bufH, 170, 20);
            result.header.AnalyzerID = Decode(enc, bufH, 190, 8);
            result.header.ClassificationEquipment = Decode(enc, bufH, 198, 8);

            // Count: "%8d\n"
            byte[] bufCnt = new byte[8];
            fs.Read(bufCnt, 0, 8); SkipNewline(fs);
            int pulseCount = TryParseInt(enc.GetString(bufCnt));

            for (int i = 0; i < pulseCount; i++)
            {
                // 한 줄 전체: C++ fprintf 4번 결합(총 502바이트) + CRLF
                // fprintf1: "%8d%30s%30s%16.7f"           =  8+30+30+16 =  84
                // fprintf2: "%16d%16d%10s%12.3f%12s"      = 16+16+10+12+12 =  66
                // fprintf3: "%80s%80s%80s%20s%20s%16.7f%8d" = 80+80+80+20+20+16+8 = 304
                // fprintf4: "%8d%8d%2d%8d%20s%2d\n"       =  8+8+2+8+20+2 =  48
                // total = 84+66+304+48 = 502
                int lineLen = 502;
                byte[] bufL = new byte[lineLen];
                fs.Read(bufL, 0, lineLen); SkipNewline(fs);

                var pulse = new AIPulseResult();
                var p = pulse.aiPulseResult;

                // fprintf1: "%8d%30s%30s%16.7f"
                p.infoPulse.lPID = TryParseLong(Decode(enc, bufL, 0, 8));
                p.strSonarName = Decode(enc, bufL, 8, 30);
                p.strPulseName = Decode(enc, bufL, 38, 30);
                p.fScore = double.TryParse(Decode(enc, bufL, 68, 16), out var fscr) ? fscr : 0.0;
                // fprintf2: "%16d%16d%10s%12.3f%12s"
                p.infoPulse.lPulseLength = TryParseLong(Decode(enc, bufL, 84, 16));
                p.infoPulse.lPri = TryParseLong(Decode(enc, bufL, 100, 16));
                p.infoPulse.strTrmMode = Decode(enc, bufL, 116, 10);
                p.infoPulse.fSpl = double.TryParse(Decode(enc, bufL, 126, 12), out var spl) ? spl : 0.0;
                p.infoPulse.strPulseModulation = Decode(enc, bufL, 138, 12);
                // fprintf3: "%80s%80s%80s%20s%20s%16.7f%8d"
                p.strGramSnippetFileName = Decode(enc, bufL, 150, 80);
                p.strAudioSnippetFileName = Decode(enc, bufL, 230, 80);
                p.strComment = Decode(enc, bufL, 310, 80);
                p.strDetectionSeaAreaLatitude = Decode(enc, bufL, 390, 20);
                p.strDetectionSeaAreaLongitude = Decode(enc, bufL, 410, 20);
                p.fDistance = double.TryParse(Decode(enc, bufL, 430, 16), out var dd) ? dd : 0.0;
                p.infoPulse.nSegmentNum = TryParseInt(Decode(enc, bufL, 446, 8));
                // fprintf4: "%8d%8d%2d%8d%20s%2d"
                p.nGramSnippetNum = TryParseInt(Decode(enc, bufL, 454, 8));
                p.nAudioSnippetNum = TryParseInt(Decode(enc, bufL, 462, 8));
                p.byteAppliedState = TryParseByte(Decode(enc, bufL, 470, 2));
                p.lDBApplyID = TryParseInt(Decode(enc, bufL, 472, 8));   // %8d (was wrongly 10)
                p.strDBAppliedDate = Decode(enc, bufL, 480, 20);
                p.byteSnippetAppliedState = TryParseByte(Decode(enc, bufL, 500, 2));

                // 세그먼트 정보
                for (int j = 0; j < p.infoPulse.nSegmentNum; j++)
                {
                    // 각 세그먼트 라인: "%8d%16.7f%16.7f%16d%5s%16.7f%2d%8d\n"
                    // = 8+16+16+16+5+16+2+8 = 87 바이트 + CRLF
                    byte[] bufS = new byte[87];
                    fs.Read(bufS, 0, bufS.Length); SkipNewline(fs);

                    var segR = new AISegmentResult
                    {
                        lSID = TryParseInt(Decode(enc, bufS, 0, 8)),
                        fStartFreq = double.TryParse(Decode(enc, bufS, 8, 16), out var sf1) ? sf1 : 0.0,
                        fEndFreq = double.TryParse(Decode(enc, bufS, 24, 16), out var ef1) ? ef1 : 0.0,
                        lSegmentLength = TryParseLong(Decode(enc, bufS, 40, 16)),
                        strModulation = Decode(enc, bufS, 56, 5),
                        fScore = double.TryParse(Decode(enc, bufS, 61, 16), out var ssf1) ? ssf1 : 0.0,
                        byteAppliedState = TryParseByte(Decode(enc, bufS, 77, 2)),
                        lDBApplyID = TryParseInt(Decode(enc, bufS, 79, 8))  // %8d (was wrongly 10)
                    };
                    pulse.listSegmentResults.Add(segR);
                }

                result.listPulseResults.Add(pulse);
            }

            return result;
        }
    }
}
