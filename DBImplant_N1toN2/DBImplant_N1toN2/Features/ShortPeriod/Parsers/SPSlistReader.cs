using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using static DBImplant_N1toN2.Features.ShortPeriod.Parsers.SdLoader;

namespace DBImplant_N1toN2.Features.ShortPeriod.Parsers
{
    public static class SpsListReader
    {
        private const int SegmentRecordSize = 64;

        static SpsListReader()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        private sealed record HeaderLayout(
            string Name,
            bool HasEventDetectType,
            bool HasCounts,
            int DirLen);

        // 후보 레이아웃들:
        // - detectingType 유무
        // - ai/tr/unk 카운트 유무
        // - dirPath 길이(0/255/260)
        private static readonly HeaderLayout[] Layouts =
        [
            new("A_detect+counts+dir255",  true,  true,  255),
            new("A_detect+counts+dir260",  true,  true,  260),

            new("B_counts+dir255",         false, true,  255),
            new("B_counts+dir260",         false, true,  260),

            new("C_dir255",                false, false, 255),
            new("C_dir260",                false, false, 260),

            new("D_nodir",                 false, false, 0),
        ];

        public static ObservableCollection<ShortPeriodEvent> ReadSpsList(string filePath)
        {
            var events = new ObservableCollection<ShortPeriodEvent>();

            if (!File.Exists(filePath))
            {
                Debug.WriteLine($"[ERR ] SPSlist 파일이 없습니다: {filePath}");
                return events;
            }

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.GetEncoding(949), leaveOpen: false);

            while (true)
            {
                long basePos = fs.Position;

                // 최소 헤더( eventId + start/end/len + segCount )도 못 읽으면 종료
                if (fs.Length - basePos < 4 + 8 + 8 + 8 + 4)
                    break;

                if (!TryReadOneEvent(br, out var ev, out int segCount))
                {
                    // 더 진행해봤자 오프셋만 더 망가질 확률이 커서 여기서 중단
                    Debug.WriteLine($"[SPS] stop at pos=0x{basePos:X}");
                    break;
                }

                // 세그먼트들 읽기
                for (int j = 0; j < segCount; j++)
                {
                    if (!TryReadSegment(br, out var seg))
                    {
                        Debug.WriteLine($"[SPS] segment read failed. event={ev.EventID}, segIndex={j}, pos=0x{fs.Position:X}");
                        return events;
                    }
                    ev.Segments.Add(seg);
                }

                // 카운트는 C++처럼 세그먼트에서 계산 (파일에 있더라도 최종값은 이게 신뢰도 높음)
                ev.SegmentCount = ev.Segments.Count;
                ev.AiSegmentCount = ev.Segments.Count(s => s.DetectingType == 1);
                ev.TrSegmentCount = ev.Segments.Count(s => s.DetectingType == 2);
                ev.UnkSegmentCount = ev.Segments.Count(s => s.DetectingType != 1 && s.DetectingType != 2);

                // 이벤트 DetectingType은 없을 수도 있으니, 비어있으면 세그먼트 분포로 채움
                if (string.IsNullOrWhiteSpace(ev.DetectingType))
                {
                    ev.DetectingType =
                        ev.TrSegmentCount > 0 && ev.AiSegmentCount == 0 ? "TR" :
                        ev.AiSegmentCount > 0 && ev.TrSegmentCount == 0 ? "AI" :
                        "UNKOWN";
                }

                events.Add(ev);
            }

            return events;
        }

        private static bool TryReadOneEvent(BinaryReader br, out ShortPeriodEvent ev, out int segmentCount)
        {
            ev = new ShortPeriodEvent();
            segmentCount = 0;

            var fs = br.BaseStream;
            long basePos = fs.Position;

            foreach (var layout in Layouts)
            {
                fs.Position = basePos;

                if (!TryReadInt32(br, out int eventId)) continue;
                if (!TryReadInt64(br, out long startTime)) continue;
                if (!TryReadInt64(br, out long endTime)) continue;
                if (!TryReadInt64(br, out long eventLength)) continue;

                uint? eventDetectTypeRaw = null;
                if (layout.HasEventDetectType)
                {
                    if (!TryReadUInt32(br, out uint dt)) continue;
                    eventDetectTypeRaw = dt;
                    // detectingType 이라면 보통 작은 값이어야 함(0~2 근처). 너무 크면 레이아웃 불일치 가능성 큼.
                    if (dt > 1000) continue;
                }

                if (!TryReadInt32(br, out int segCount)) continue;
                if (segCount < 0 || segCount > 200000) continue;

                int ai = 0, tr = 0, unk = 0;
                if (layout.HasCounts)
                {
                    if (!TryReadInt32(br, out ai)) continue;
                    if (!TryReadInt32(br, out tr)) continue;
                    if (!TryReadInt32(br, out unk)) continue;

                    // 카운트가 들어있다면 보통 segCount 범위 내여야 함
                    if (ai < 0 || tr < 0 || unk < 0) continue;
                    if (ai > segCount || tr > segCount || unk > segCount) continue;
                }

                // manual/auto (C++에서 pQEvnt->m_TmanualFauto 로 보이는 값)
                if (!TryReadUInt32(br, out uint manualAuto)) continue;

                string dirPath = string.Empty;
                if (layout.DirLen > 0)
                {
                    if (!TryReadBytesExact(br, layout.DirLen, out var dirBuf)) continue;
                    dirPath = DecodeFixedString(dirBuf, Encoding.GetEncoding(949));
                }

                // 남은 바이트가 최소 세그먼트 레코드 크기 * segCount 보다 작으면 레이아웃 불일치
                long remain = fs.Length - fs.Position;
                long need = (long)segCount * SegmentRecordSize;
                if (remain < need) continue;

                // segCount>0 이면 첫 세그먼트를 “미리” 읽어서 값이 정상 범위인지 확인
                long afterHeader = fs.Position;
                if (segCount > 0)
                {
                    if (!TryReadSegment(br, out var probe)) continue;
                    if (!LooksLikeValidSegment(probe)) continue;
                    fs.Position = afterHeader; // 검증용으로 한 번 읽었으니 되돌림
                }

                // 여기까지 오면 이 레이아웃이 맞다고 판단
                segmentCount = segCount;

                ev.EventID = eventId;
                ev.StartTime = startTime;
                ev.EndTime = endTime;
                ev.EventLength = eventLength;
                ev.SegmentCount = segCount;
                ev.ManualOrAuto = manualAuto != 0;
                ev.DirPath = dirPath;

                // event detecting type 이 실제로 존재하는 레이아웃인 경우만 문자열로 채움
                if (eventDetectTypeRaw.HasValue)
                {
                    ev.DetectingType = eventDetectTypeRaw.Value switch
                    {
                        1 => "AI",
                        2 => "TR",
                        _ => "UNKOWN"
                    };
                }

                Debug.WriteLine($"[SPS] layout={layout.Name}, eventId={eventId}, segCount={segCount}, pos=0x{basePos:X}");
                return true;
            }

            // 모든 레이아웃이 실패
            fs.Position = basePos;
            return false;
        }

        private static bool TryReadSegment(BinaryReader br, out ShortPeriodSegment seg)
        {
            seg = new ShortPeriodSegment();

            if (!TryReadUInt32(br, out uint segId)) return false;
            if (!TryReadInt64(br, out long segStart)) return false;
            if (!TryReadInt64(br, out long segEnd)) return false;
            if (!TryReadInt64(br, out long segLen)) return false;
            if (!TryReadSingle(br, out float ldaX)) return false;
            if (!TryReadSingle(br, out float ldaY)) return false;
            if (!TryReadUInt32(br, out uint segType)) return false;
            if (!TryReadUInt32(br, out uint frameCount)) return false;
            if (!TryReadSingle(br, out float startFreq)) return false;
            if (!TryReadSingle(br, out float endFreq)) return false;
            if (!TryReadInt32(br, out int mod)) return false;
            if (!TryReadSingle(br, out float centerFreq)) return false;
            if (!TryReadSingle(br, out float bandwidth)) return false;

            seg.SegmentID = (int)segId;
            seg.StartTime = segStart;
            seg.EndTime = segEnd;
            seg.Length = segLen;
            seg.LDAx = ldaX;
            seg.LDAy = ldaY;
            seg.DetectingType = (int)segType;
            seg.FrameCount = (int)frameCount;
            seg.StartFreq = startFreq;
            seg.EndFreq = endFreq;
            seg.Modulation = mod;
            seg.CenterFreq = centerFreq;
            seg.BandWidth = bandwidth;

            return true;
        }

        private static bool LooksLikeValidSegment(ShortPeriodSegment s)
        {
            // “완벽 검증”이 아니라 레이아웃 판별용 휴리스틱
            if (s.SegmentID < 0 || s.SegmentID > 1_000_000_000) return false;
            if (s.StartTime > 0 && s.EndTime > 0 && s.EndTime < s.StartTime) return false;
            if (s.Length < 0) return false;

            if (!double.IsFinite(s.LDAx) || !double.IsFinite(s.LDAy)) return false;

            // 주파수는 장비/데이터에 따라 다르지만, 음수/NaN/말도 안되게 큰 값만 컷
            if (!double.IsFinite(s.StartFreq) || !double.IsFinite(s.EndFreq)) return false;
            if (s.StartFreq < 0 || s.EndFreq < 0) return false;
            if (s.StartFreq > 1_000_000 || s.EndFreq > 1_000_000) return false;

            if (!double.IsFinite(s.CenterFreq) || !double.IsFinite(s.BandWidth)) return false;
            if (s.CenterFreq < 0 || s.CenterFreq > 1_000_000) return false;
            if (s.BandWidth < 0 || s.BandWidth > 1_000_000) return false;

            if (s.FrameCount < 0 || s.FrameCount > 10_000_000) return false;

            // DetectingType은 보통 작은 enum 값이길 기대
            if (s.DetectingType < 0 || s.DetectingType > 1000) return false;

            return true;
        }

        private static string DecodeFixedString(byte[] bytes, Encoding enc)
        {
            var s = enc.GetString(bytes);
            int nul = s.IndexOf('\0');
            if (nul >= 0) s = s[..nul];
            return s.Trim();
        }

        /* ---- TryRead helpers (EOF/부분읽기 방지) ---- */

        private static bool TryReadBytesExact(BinaryReader br, int len, out byte[] bytes)
        {
            bytes = br.ReadBytes(len);
            return bytes.Length == len;
        }

        private static bool TryReadInt32(BinaryReader br, out int v)
        {
            v = 0;
            var s = br.BaseStream;
            if (s.Length - s.Position < 4) return false;
            v = br.ReadInt32();
            return true;
        }

        private static bool TryReadUInt32(BinaryReader br, out uint v)
        {
            v = 0;
            var s = br.BaseStream;
            if (s.Length - s.Position < 4) return false;
            v = br.ReadUInt32();
            return true;
        }

        private static bool TryReadInt64(BinaryReader br, out long v)
        {
            v = 0;
            var s = br.BaseStream;
            if (s.Length - s.Position < 8) return false;
            v = br.ReadInt64();
            return true;
        }

        private static bool TryReadSingle(BinaryReader br, out float v)
        {
            v = 0;
            var s = br.BaseStream;
            if (s.Length - s.Position < 4) return false;
            v = br.ReadSingle();
            return true;
        }
    }
}
