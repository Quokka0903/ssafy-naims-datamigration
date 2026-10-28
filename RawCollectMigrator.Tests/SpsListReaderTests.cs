using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RawCollectMigrator.Features.ExtractRaw;
using Xunit;

namespace RawCollectMigrator.Tests
{
    /// <summary>
    /// SPSlist.txt 파서 단위 테스트.
    ///
    /// SPSlist 는 확장자만 .txt 인 바이너리이고, 두 레거시가 서로 다른 방식으로 쓴다.
    ///   · AAS  — Library_AASBase\UtilFileManage.cpp:6654 WriteQuickShortPeriodEvent 가
    ///            필드를 하나씩 Write → 패딩 없음. 이벤트 307B / 세그먼트 64B.
    ///   · CDMU — USPASK_APU\QuickLeftDigitalSaveView.cpp:302 가
    ///            ff.Write(pSPEvent, sizeof(struct) - sizeof(list)) 로 구조체를 통째로 Write
    ///            → 정렬 패딩이 그대로 파일에 박힌다. 이벤트 316B(311+꼬리5) / 세그먼트 68B(x86).
    /// 여기서는 두 포맷의 바이트를 직접 조립해, 파서가 레이아웃을 자동 판별하고
    /// 같은 값을 돌려주는지(= 원천이 무엇이든 이관 결과가 같은지) 검증한다.
    /// </summary>
    public sealed class SpsListReaderTests
    {
        // ── 바이트 조립 도우미 ───────────────────────────────────────────────

        /// <summary>이벤트 헤더 필드를 순서대로 쓴다(패딩 제어는 호출자가 인자로 지정).</summary>
        /// <param name="bw">대상 라이터.</param>
        /// <param name="eventId">EventID.</param>
        /// <param name="start">시작 시각(레거시 원시값).</param>
        /// <param name="end">종료 시각.</param>
        /// <param name="length">이벤트 길이.</param>
        /// <param name="detectType">검출 타입 원시값(1=AI, 2=TR).</param>
        /// <param name="segCount">뒤따르는 세그먼트 레코드 수.</param>
        /// <param name="ai">헤더에 박힌 AI 카운터(실제 세그먼트 수와 달라도 된다).</param>
        /// <param name="tr">헤더에 박힌 TR 카운터.</param>
        /// <param name="unk">헤더에 박힌 UNK 카운터.</param>
        /// <param name="dir">디렉터리 경로(CP949 고정폭 255B).</param>
        /// <param name="padded">true면 EventID 뒤에 4B 정렬 패딩을 넣는다(CDMU 구조체 통째 산출물).</param>
        /// <param name="trailPad">헤더 끝(dir 이후)에 붙일 꼬리 패딩 바이트 수.</param>
        private static void WriteEvent(BinaryWriter bw, int eventId, long start, long end, long length,
                                       uint detectType, int segCount, int ai, int tr, int unk,
                                       string dir, bool padded, int trailPad)
        {
            bw.Write(eventId);
            if (padded) bw.Write(new byte[4]);
            bw.Write(start);
            bw.Write(end);
            bw.Write(length);
            bw.Write(detectType);
            bw.Write(segCount);
            bw.Write(ai);
            bw.Write(tr);
            bw.Write(unk);
            bw.Write((uint)0);                          // ManualOrAuto = 자동
            var buf = new byte[255];
            var raw = Encoding.GetEncoding(949).GetBytes(dir);
            Array.Copy(raw, buf, Math.Min(raw.Length, 254));   // 마지막 1바이트는 NUL 종단으로 남긴다
            bw.Write(buf);
            if (trailPad > 0) bw.Write(new byte[trailPad]);
        }

        /// <summary>세그먼트 레코드 1건을 쓴다.</summary>
        /// <param name="bw">대상 라이터.</param>
        /// <param name="segId">SegmentID.</param>
        /// <param name="detectType">검출 타입(1=AI, 2=TR).</param>
        /// <param name="padded">true면 SegmentID 뒤에 4B 정렬 패딩을 넣는다.</param>
        /// <param name="recordSize">레코드 총 크기(부족분은 0으로 채운다). AAS 64 / CDMU x86 68.</param>
        private static void WriteSegment(BinaryWriter bw, uint segId, uint detectType, bool padded, int recordSize)
        {
            long begin = bw.BaseStream.Position;
            bw.Write(segId);
            if (padded) bw.Write(new byte[4]);
            bw.Write(1_000L);                   // StartTime
            bw.Write(2_000L);                   // EndTime
            bw.Write(1_000L);                   // Length
            bw.Write(0.25f);                    // LDAx
            bw.Write(0.50f);                    // LDAy
            bw.Write(detectType);
            bw.Write((uint)7);                  // FrameCount
            bw.Write(100.0f);                   // StartFreq
            bw.Write(200.0f);                   // EndFreq
            bw.Write(3);                        // Modulation
            bw.Write(150.0f);                   // CenterFreq
            bw.Write(100.0f);                   // BandWidth
            int written = (int)(bw.BaseStream.Position - begin);
            if (written < recordSize) bw.Write(new byte[recordSize - written]);
        }

        /// <summary>바이트 배열을 임시 파일로 떨군다(테스트 종료 시 호출자가 지운다).</summary>
        /// <param name="bytes">파일 내용.</param>
        /// <returns>생성된 임시 파일 경로.</returns>
        private static string TempFile(byte[] bytes)
        {
            var path = Path.Combine(Path.GetTempPath(), "sps_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllBytes(path, bytes);
            return path;
        }

        /// <summary>AAS(packed) 포맷 SPSlist 를 조립한다 — 이벤트 307B + 세그먼트 64B×N.</summary>
        /// <param name="segCounts">이벤트별 세그먼트 개수.</param>
        /// <returns>파일 바이트.</returns>
        private static byte[] BuildAas(params int[] segCounts)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            for (int i = 0; i < segCounts.Length; i++)
            {
                int n = segCounts[i];
                WriteEvent(bw, i + 1, 10_000 + i, 20_000 + i, 10_000, 1, n, n, 0, 0,
                           @"Z:\MSU\수집원음자료", padded: false, trailPad: 0);
                for (int j = 0; j < n; j++) WriteSegment(bw, (uint)(j + 1), 1, padded: false, recordSize: 64);
            }
            bw.Flush();
            return ms.ToArray();
        }

        /// <summary>CDMU(구조체 통째, x86) 포맷 SPSlist 를 조립한다 — 이벤트 316B + 세그먼트 68B×N.</summary>
        /// <param name="segCounts">이벤트별 세그먼트 개수.</param>
        /// <returns>파일 바이트.</returns>
        private static byte[] BuildCdmu(params int[] segCounts)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            for (int i = 0; i < segCounts.Length; i++)
            {
                int n = segCounts[i];
                WriteEvent(bw, i + 1, 10_000 + i, 20_000 + i, 10_000, 1, n, n, 0, 0,
                           @"D:\USPASK_DATA\단주기원음자료", padded: true, trailPad: 5);
                for (int j = 0; j < n; j++) WriteSegment(bw, (uint)(j + 1), 1, padded: true, recordSize: 68);
            }
            bw.Flush();
            return ms.ToArray();
        }

        // ── 레코드 크기 계약 ─────────────────────────────────────────────────

        /// <summary>
        /// AAS 는 이벤트 307B/세그먼트 64B, CDMU 는 316B/68B 여야 한다.
        /// 이 숫자가 어긋나면 파서의 레이아웃 후보 전체가 무의미해지므로 조립기부터 못박아 둔다.
        /// </summary>
        [Fact]
        public void 조립기가_레거시_레코드_크기를_지킨다()
        {
            Assert.Equal(307 + 64, BuildAas(1).Length);
            Assert.Equal(316 + 68, BuildCdmu(1).Length);
        }

        // ── 두 레거시 포맷 모두 읽는다 ───────────────────────────────────────

        /// <summary>AAS(packed) 파일을 읽어 이벤트·세그먼트 수와 레이아웃 표기를 확인한다.</summary>
        [Fact]
        public void AAS_packed_포맷을_읽는다()
        {
            var path = TempFile(BuildAas(2, 1, 0));
            try
            {
                var (events, layout, error) = SpsListReader.Read(path);
                Assert.Null(error);
                Assert.Equal(3, events.Count);
                Assert.Equal(new[] { 2, 1, 0 }, events.ConvertAll(e => e.SegmentCount).ToArray());
                Assert.Equal("A_detect+counts+dir255", layout);
                Assert.StartsWith("AAS packed/", SpsListReader.DescribeLayout(layout));
            }
            finally { File.Delete(path); }
        }

        /// <summary>CDMU(구조체 통째) 파일을 읽어 같은 결과가 나오는지 확인한다.</summary>
        [Fact]
        public void CDMU_padded_포맷을_읽는다()
        {
            var path = TempFile(BuildCdmu(2, 1, 0));
            try
            {
                var (events, layout, error) = SpsListReader.Read(path);
                Assert.Null(error);
                Assert.Equal(3, events.Count);
                Assert.Equal(new[] { 2, 1, 0 }, events.ConvertAll(e => e.SegmentCount).ToArray());
                Assert.Equal("P_pad+dir255+t5+seg68", layout);
                Assert.StartsWith("CDMU padded/", SpsListReader.DescribeLayout(layout));
            }
            finally { File.Delete(path); }
        }

        /// <summary>
        /// 같은 내용을 AAS/CDMU 두 포맷으로 쓰면 파싱 결과(이벤트 ID·시각·세그먼트 값)가 동일해야 한다.
        /// 이관 결과가 원본을 쓴 체계에 따라 달라지면 안 된다는 계약.
        /// </summary>
        [Fact]
        public void 두_포맷의_파싱_결과가_같다()
        {
            var aasPath = TempFile(BuildAas(2, 1));
            var cdmuPath = TempFile(BuildCdmu(2, 1));
            try
            {
                var a = SpsListReader.Read(aasPath).events;
                var c = SpsListReader.Read(cdmuPath).events;
                Assert.Equal(a.Count, c.Count);
                for (int i = 0; i < a.Count; i++)
                {
                    Assert.Equal(a[i].EventID, c[i].EventID);
                    Assert.Equal(a[i].StartTime, c[i].StartTime);
                    Assert.Equal(a[i].EndTime, c[i].EndTime);
                    Assert.Equal(a[i].SegmentCount, c[i].SegmentCount);
                    for (int j = 0; j < a[i].Segments.Count; j++)
                    {
                        Assert.Equal(a[i].Segments[j].SegmentID, c[i].Segments[j].SegmentID);
                        Assert.Equal(a[i].Segments[j].StartFreq, c[i].Segments[j].StartFreq);
                        Assert.Equal(a[i].Segments[j].BandWidth, c[i].Segments[j].BandWidth);
                        Assert.Equal(a[i].Segments[j].DetectingType, c[i].Segments[j].DetectingType);
                    }
                }
            }
            finally { File.Delete(aasPath); File.Delete(cdmuPath); }
        }

        // ── 회귀: 헤더 카운터가 세그먼트 수를 넘어도 거부하지 않는다 ─────────

        /// <summary>
        /// [NAIMS-ISSUE:SPSLIST-COUNTS-OVERFLOW] 헤더의 AI/TR/UNK 카운터는 표시용이라
        /// SegmentCount 를 넘을 수 있다(레거시가 파일값 위에 ++ 를 덧쌓아 다시 저장하기 때문).
        /// 실측 표본: 수집원음자료\ISUS83_20241125132026 의 SPSlist 는 TR=2 / 세그먼트 1개.
        /// 예전 게이트는 이런 정상 파일을 통째로 거부해 항목 전체가 이관 실패했다.
        /// 실제 개수는 세그먼트를 직접 세어 다시 채워야 한다.
        /// </summary>
        [Fact]
        public void 헤더_카운터가_세그먼트수를_넘어도_읽고_실제값으로_다시_센다()
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                // 세그먼트는 1개(TR)인데 헤더 TR 카운터는 2 — 열었다 저장하며 누적된 값
                WriteEvent(bw, 1, 10_000, 20_000, 10_000, detectType: 2, segCount: 1,
                           ai: 0, tr: 2, unk: 0, dir: @"Z:\MSU", padded: false, trailPad: 0);
                WriteSegment(bw, 1, detectType: 2, padded: false, recordSize: 64);
            }
            var path = TempFile(ms.ToArray());
            try
            {
                var (events, _, error) = SpsListReader.Read(path);
                Assert.Null(error);
                var ev = Assert.Single(events);
                Assert.Equal(1, ev.SegmentCount);
                Assert.Equal(1, ev.TrSegmentCount);      // 헤더의 2 가 아니라 실제로 센 1
                Assert.Equal(0, ev.AiSegmentCount);
            }
            finally { File.Delete(path); }
        }

        // ── 손상/빈 파일 ─────────────────────────────────────────────────────

        /// <summary>0바이트 SPSlist 는 오류가 아니라 "이벤트 없음"이다(협대역 폴더에 흔하다).</summary>
        [Fact]
        public void 빈_파일은_오류가_아니다()
        {
            var path = TempFile(Array.Empty<byte>());
            try
            {
                var (events, _, error) = SpsListReader.Read(path);
                Assert.Null(error);
                Assert.Empty(events);
            }
            finally { File.Delete(path); }
        }

        /// <summary>없는 파일도 오류가 아니라 빈 결과다.</summary>
        [Fact]
        public void 없는_파일은_빈_결과다()
        {
            var (events, _, error) = SpsListReader.Read(Path.Combine(Path.GetTempPath(), "no_such_sps.txt"));
            Assert.Null(error);
            Assert.Empty(events);
        }

        /// <summary>
        /// 뒤가 잘린 파일은 오류로 올라와야 한다 — 부분 결과가 "완료" 로 위장되면 누락을 못 찾는다
        /// ([NAIMS-ISSUE:PARSE-SILENT-PARTIAL]).
        /// </summary>
        [Fact]
        public void 잘린_파일은_오류를_돌려준다()
        {
            var full = BuildAas(3);
            var path = TempFile(full[..(full.Length - 20)]);   // 마지막 세그먼트를 잘라낸다
            try
            {
                var (_, layout, error) = SpsListReader.Read(path);
                Assert.NotNull(error);
                Assert.Null(layout);
            }
            finally { File.Delete(path); }
        }

        /// <summary>DRM 래핑 등 SPSlist 가 아닌 바이너리는 조용히 0건이 아니라 오류로 걸러져야 한다.</summary>
        [Fact]
        public void SPSlist가_아닌_파일은_오류를_돌려준다()
        {
            var junk = Encoding.ASCII.GetBytes("<!-- FasooSecureContainer - Ver FSN 1.1 -->");
            var padded = new byte[2048];
            junk.CopyTo(padded, 0);
            for (int i = junk.Length; i < padded.Length; i++) padded[i] = (byte)(i * 37 % 251 + 1);
            var path = TempFile(padded);
            try
            {
                var (_, layout, error) = SpsListReader.Read(path);
                Assert.NotNull(error);
                Assert.Null(layout);
            }
            finally { File.Delete(path); }
        }
    }
}
