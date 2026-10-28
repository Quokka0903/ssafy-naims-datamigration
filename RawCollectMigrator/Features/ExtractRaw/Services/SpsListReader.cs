using System.IO;
using System.Text;

namespace RawCollectMigrator.Features.ExtractRaw
{
    /// <summary>
    /// SPSlist(단주기 신호처리 이벤트/세그먼트 인덱스, 바이너리).
    /// Main 프로젝트 SpsListReader 이식 — 여러 헤더 레이아웃 자동 탐지.
    /// </summary>
    public static class SpsListReader
    {
        private const int SegmentRecordSize = 64;   // packed(필드 단위) 세그먼트 크기

        static SpsListReader() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }

        // [NAIMS-ISSUE:SPSLIST-PADDED] 두 레거시가 SPSlist 를 서로 다르게 쓴다 — 원본 코드로 확정(2026-08-19).
        //   · AAS(2018~) — Library_AASBase\UtilFileManage.cpp:6654 WriteQuickShortPeriodEvent /
        //     :6696 WriteQuickShortPeriodSegment 는 필드를 하나씩 Write 한다 → 패딩 없음(packed).
        //       이벤트 307B = 4(EventID)+8+8+8+4(detect)+4(segCnt)+4+4+4(AI/TR/UNK)+4(수동자동)+255(dir)
        //       세그먼트 64B = 4+8+8+8+4+4+4+4+4+4+4+4+4
        //   · USPASK/CDMU(~2021) — QuickLeftDigitalSaveView.cpp:302 · RawListManDlg.cpp:809 등은
        //     ff.Write(pSPEvent, sizeof(struct) - sizeof(list)) 로 구조체를 통째 쓴다.
        //     FileReadWriteFunc.h:145 의 클래스 정의(long → __int64 ×3 → … → char[255] → std::list)에
        //     x86 /Zp8 을 적용하면: EventID 뒤 4B 정렬 패딩 + 필드 끝 311B + list 정렬용 꼬리 패딩까지
        //     sizeof=328 · sizeof(list)=12 → 파일에 쓰이는 이벤트 = 316B(= 311 + 꼬리 5B).
        //     세그먼트는 UINT 뒤 4B 패딩 포함 필드 68B, sizeof=80 · list=12 → 68B(꼬리 0).
        //     x64 빌드라면 sizeof(list)=24 라 이벤트는 그대로 316B(꼬리 5B), 세그먼트만 72B 가 된다.
        //     ※ 읽기 쪽(ShortSignalProcessMainDlg.cpp:1508)의 "-LIST_SIZE_MINUS(=4) 후 4B 추가 읽기"는
        //       Globalvariables.h:5 주석대로 std::list 크기가 컴파일러마다 달라 뒤 메모리를 덮어쓰는 것을
        //       막으려는 조치일 뿐, 소비하는 총 바이트 수는 같다 → 파일 포맷은 바뀌지 않는다.
        //   실측(2026-08-19, SPSlist 95개 전수): AAS packed 79 / CDMU padded(t5+seg68) 3 / 0바이트 11 /
        //     Fasoo DRM 래핑 2(원본 아님). CDMU 표본 = NAIMS1 실데이터 ISUS90_20191126151223_00_000_001.sd.
        //   그래서 packed 를 먼저 시도하고, 안 되면 padded 변형들을 꼬리 패딩·세그먼트 크기 후보와 함께
        //   시도한다. 어느 후보든 파일 끝에 정확히 맞아떨어져야 채택된다(ParseWith + LooksLikeValidSegment).
        /// <param name="Padded">EventID/SegmentID 뒤에 4바이트 정렬 패딩이 있는가(구조체 통째 읽기 산출물).</param>
        /// <param name="EvtTrailPad">이벤트 헤더 끝(dir 이후)에 붙는 꼬리 패딩 바이트 수.</param>
        /// <param name="SegSize">세그먼트 레코드 1건의 전체 바이트 수(패딩 포함).</param>
        private sealed record HeaderLayout(string Name, bool HasEventDetectType, bool HasCounts, int DirLen,
                                           bool Padded = false, int EvtTrailPad = 0, int SegSize = SegmentRecordSize);

        private static readonly HeaderLayout[] Layouts =
        {
            // ── packed (AAS) : 현장 표준. 반드시 먼저 시도해 기존 동작을 보존한다.
            new("A_detect+counts+dir255",  true,  true,  255),
            new("A_detect+counts+dir260",  true,  true,  260),
            new("B_counts+dir255",         false, true,  255),
            new("B_counts+dir260",         false, true,  260),
            new("C_dir255",                false, false, 255),
            new("C_dir260",                false, false, 260),
            new("D_nodir",                 false, false, 0),

            // ── padded (USPASK/CDMU raw struct) : 필드 끝 311B + 꼬리 패딩, 세그먼트 68B(x86) / 72B(x64)
            //   x86(VS2013/VS2015, 실제 CDMU 빌드)를 먼저 둔다 — NAIMS1 실데이터가 t5+seg68 이다.
            new("P_pad+dir255+t5+seg68",   true,  true,  255, true, 5, 68),
            new("P_pad+dir255+t5+seg72",   true,  true,  255, true, 5, 72),
            new("P_pad+dir255+t1+seg68",   true,  true,  255, true, 1, 68),
            new("P_pad+dir255+t9+seg68",   true,  true,  255, true, 9, 68),
            new("P_pad+dir255+t1+seg72",   true,  true,  255, true, 1, 72),
            new("P_pad+dir255+t9+seg72",   true,  true,  255, true, 9, 72),
            new("P_pad+dir260+t5+seg68",   true,  true,  260, true, 5, 68),
            new("P_pad+dir260+t5+seg72",   true,  true,  260, true, 5, 72),
            new("P_pad+dir260+t1+seg68",   true,  true,  260, true, 1, 68),
            new("P_pad+dir260+t1+seg72",   true,  true,  260, true, 1, 72),
        };

        /// <summary>
        /// SPSlist 바이너리 파일을 열어 이벤트(SdEvent)와 하위 세그먼트(SdSegment)를 순차 파싱한다.
        /// 파일 IO: FileStream을 읽기 전용/공유읽기로 열고 CP949(EUC-KR) BinaryReader로 처리한다.
        /// 파싱 분기: 스트림 끝까지 루프하며 <see cref="TryReadOneEvent"/>로 이벤트 헤더를,
        /// 이어서 헤더가 알려준 세그먼트 개수만큼 <see cref="TryReadSegment"/>로 세그먼트를 읽는다.
        /// 이벤트당 남은 바이트가 최소 헤더 크기(4+8+8+8+4)에 못 미치면 정상 종료로 간주한다.
        /// 세그먼트를 다 읽은 뒤 DetectingType(1=AI, 2=TR, 그 외=UNK)별로 개수를 집계하고,
        /// 이벤트 헤더에 detect 타입이 없었으면 세그먼트 집계 결과로 이벤트 타입(AI/TR/UNKOWN)을 역추론한다.
        /// 파일이 없으면 빈 리스트(성공)를 반환한다. 파싱 실패(세그먼트 절단, 헤더 인식 불가 지점에
        /// 상당한 잔여 바이트, 예외 등)는 삼키지 않고 반환 튜플의 error 문자열에 담아
        /// 호출부가 "파싱 실패"를 항목 오류로 표기할 수 있게 한다(부분 결과의 "완료" 위장 방지).
        /// </summary>
        /// <param name="filePath">읽어들일 SPSlist 바이너리 파일의 전체 경로.</param>
        /// <returns>
        /// (이벤트 목록, 채택된 레이아웃 이름, 오류 문자열). error 가 null 이면 성공,
        /// null 이 아니면 실패이며 events 는 실패 시점까지의 부분 결과다.
        /// layout 은 실제 이관에 쓰인 원본 포맷을 로그에 남기기 위한 값으로,
        /// 성공 시 <see cref="Layouts"/> 의 이름(예: A_detect+counts+dir255 = AAS packed,
        /// P_pad+dir255+t5+seg68 = USPASK/CDMU 구조체 통째)이고 실패 시 null 이다.
        /// </returns>
        public static (List<SdEvent> events, string? layout, string? error) Read(string filePath)
        {
            if (!File.Exists(filePath)) return (new List<SdEvent>(), null, null);

            try
            {
                // [NAIMS-ISSUE:SPSLIST-LAYOUT-WHOLEFILE] 레이아웃은 파일 전체에 하나만 쓰인다.
                //   예전처럼 "이벤트마다" 레이아웃을 새로 고르면, 세그먼트가 0개인 이벤트에서는
                //   검증할 세그먼트가 없어 엉뚱한 후보가 채택되고 이후 전부 어긋난다(실측 확인).
                //   → 후보 레이아웃으로 파일을 통째 파싱해 보고, "파일 끝에 정확히 맞아떨어지는"
                //     레이아웃을 채택한다. 하나도 없으면 가장 많이 읽어낸 결과를 오류와 함께 돌려준다.
                var data = File.ReadAllBytes(filePath);
                if (data.Length == 0) return (new List<SdEvent>(), "빈 파일", null);

                List<SdEvent>? bestEvents = null; string? bestError = null; long bestConsumed = -1;

                foreach (var layout in Layouts)
                {
                    var (evs, consumed, err) = ParseWith(data, layout);
                    if (err == null && consumed == data.Length && evs.Count > 0)
                        return (evs, layout.Name, null);       // 완전 일치 — 이 레이아웃이 정답
                    if (consumed > bestConsumed) { bestConsumed = consumed; bestEvents = evs; bestError = err; }
                }

                var partial = bestEvents ?? new List<SdEvent>();
                var reason = bestError ?? $"어떤 레이아웃으로도 파일 끝과 맞지 않음 " +
                                          $"(읽은 {bestConsumed:N0}/{data.Length:N0}바이트, 이벤트 {partial.Count}개)";
                return (partial, null, reason);
            }
            catch (Exception ex)
            {
                // 침묵 부분-결과 금지: 실패 사유를 호출부로 올려 "완료" 로 위장되지 않게 한다.
                return (new List<SdEvent>(), null, "SPSlist 파싱 오류: " + ex.Message);
            }
        }

        /// <summary>
        /// <see cref="Read"/> 가 돌려준 레이아웃 이름을 사람이 읽는 원천 표기로 바꾼다.
        /// 로그·화면에 "어느 레거시가 쓴 파일을 읽었는가"를 남기기 위한 것으로,
        /// P_ 로 시작하면 USPASK/CDMU 가 구조체를 통째 쓴 파일, 그 외는 AAS 가 필드 단위로 쓴 파일이다.
        /// </summary>
        /// <param name="layoutName">Read 가 돌려준 레이아웃 이름(null 이면 빈 문자열).</param>
        /// <returns>예: "AAS packed/A_detect+counts+dir255", "CDMU padded/P_pad+dir255+t5+seg68".</returns>
        public static string DescribeLayout(string? layoutName)
        {
            if (string.IsNullOrEmpty(layoutName)) return "";
            if (layoutName == "빈 파일") return layoutName;
            var kind = layoutName.StartsWith("P_", StringComparison.Ordinal) ? "CDMU padded" : "AAS packed";
            return $"{kind}/{layoutName}";
        }

        /// <summary>
        /// 지정한 레이아웃 하나만으로 파일 전체를 파싱해 본다(레이아웃 확정용).
        /// 이벤트 헤더 → 세그먼트 N개를 반복하며, 세그먼트는 <see cref="LooksLikeValidSegment"/>로 검증한다.
        /// 남은 바이트가 최소 헤더 크기에 못 미치면 정상 종료로 보고 소비량을 돌려준다.
        /// </summary>
        /// <param name="data">파일 전체 바이트.</param>
        /// <param name="layout">시도할 레이아웃(패딩 유무·dir 길이·세그먼트 크기 포함).</param>
        /// <returns>(파싱된 이벤트, 소비한 바이트 수, 오류 사유 또는 null)</returns>
        private static (List<SdEvent> events, long consumed, string? error) ParseWith(byte[] data, HeaderLayout layout)
        {
            var events = new List<SdEvent>();
            using var ms = new MemoryStream(data, writable: false);
            using var br = new BinaryReader(ms, Encoding.GetEncoding(949), leaveOpen: false);

            const int MinHeaderBytes = 4 + 8 + 8 + 8 + 4;
            int idx = 0;
            while (true)
            {
                long basePos = ms.Position;
                if (data.Length - basePos < MinHeaderBytes) break;   // 정상 종료(잔여는 헤더 미만)

                if (!TryReadOneEvent(br, layout, out var ev, out int segCount))
                    return (events, basePos, $"이벤트 헤더 인식 실패 (offset {basePos:N0}, " +
                                             $"잔여 {data.Length - basePos:N0}바이트, 파싱된 이벤트 {events.Count}개)");

                for (int j = 0; j < segCount; j++)
                {
                    if (!TryReadSegment(br, layout, out var seg) || !LooksLikeValidSegment(seg))
                        return (events, basePos, $"세그먼트 절단/불량 (evt={ev.EventID}, seg {j + 1}/{segCount})");
                    seg.Index = j;
                    ev.Segments.Add(seg);
                }

                ev.SegmentCount = ev.Segments.Count;
                ev.AiSegmentCount = ev.Segments.Count(s => s.DetectingType == 1);
                ev.TrSegmentCount = ev.Segments.Count(s => s.DetectingType == 2);
                ev.UnkSegmentCount = ev.Segments.Count(s => s.DetectingType != 1 && s.DetectingType != 2);

                if (string.IsNullOrWhiteSpace(ev.DetectingType))
                {
                    ev.DetectingType =
                        ev.TrSegmentCount > 0 && ev.AiSegmentCount == 0 ? "TR" :
                        ev.AiSegmentCount > 0 && ev.TrSegmentCount == 0 ? "AI" : "UNKOWN";
                }
                ev.Index = idx++;
                events.Add(ev);
            }
            return (events, ms.Position, null);
        }

        /// <summary>
        /// 현재 스트림 위치에서 이벤트 헤더 1건을 읽는다. AAS 빌드마다 헤더 레이아웃이 달라서
        /// <see cref="Layouts"/>에 정의된 후보 레이아웃을 순서대로 시도하는 자동 탐지 방식이다.
        /// 각 시도마다 스트림을 진입 위치(basePos)로 되감고 아래 고정폭 필드를 순서대로 파싱한다:
        ///   EventID(Int32,4B) → StartTime(Int64,8B) → EndTime(Int64,8B) → EventLength(Int64,8B)
        ///   → [레이아웃에 detect 타입이 있으면] EventDetectType(UInt32,4B)
        ///   → SegmentCount(Int32,4B)
        ///   → [레이아웃에 counts가 있으면] AI/TR/UNK 개수(각 Int32,4B)
        ///   → ManualOrAuto(UInt32,4B, 0이면 수동/그 외 자동)
        ///   → [DirLen>0이면] 디렉터리 경로 고정폭 바이트(CP949, NUL 종단).
        /// 유효성 게이트(잘못된 레이아웃 배제): detect 타입 &gt; 1000, 세그먼트 개수 음수/20만 초과,
        /// counts 음수 또는 세그먼트 개수 초과, 세그먼트 영역이 파일 잔여 바이트보다 큰 경우 다음 레이아웃으로 넘어간다.
        /// 추가 검증: 세그먼트가 1개 이상이면 첫 세그먼트를 실제로 읽어(probe) <see cref="LooksLikeValidSegment"/>로
        /// 그럴듯한지 확인한 뒤 위치를 헤더 직후로 되돌린다(재파싱을 위해 소비 취소).
        /// 첫 이벤트의 detect 타입이 있으면 즉시 문자열(AI/TR/UNKOWN)로 매핑해 채운다.
        /// </summary>
        /// <param name="br">CP949로 구성된 BinaryReader(리틀엔디언 고정폭 필드 읽기용).</param>
        /// <param name="ev">파싱에 성공하면 채워진 이벤트. 실패 시 빈 인스턴스.</param>
        /// <param name="segmentCount">이 이벤트에 뒤따르는 세그먼트 레코드 개수(호출자가 반복 읽기에 사용).</param>
        /// <returns>어느 한 레이아웃으로 유효 헤더를 읽어내면 true, 모든 레이아웃 실패 시 위치를 되돌리고 false.</returns>
        private static bool TryReadOneEvent(BinaryReader br, HeaderLayout layout, out SdEvent ev, out int segmentCount)
        {
            ev = new SdEvent();
            segmentCount = 0;

            var fs = br.BaseStream;
            long basePos = fs.Position;

            bool Fail() { fs.Position = basePos; return false; }

            if (!TryReadInt32(br, out int eventId)) return Fail();
            // 구조체 통째 읽기 산출물은 long(4B) 뒤 __int64 앞에 4B 정렬 패딩이 있다.
            if (layout.Padded && !TrySkip(br, 4)) return Fail();
            if (!TryReadInt64(br, out long startTime)) return Fail();
            if (!TryReadInt64(br, out long endTime)) return Fail();
            if (!TryReadInt64(br, out long eventLength)) return Fail();

            uint? eventDetectTypeRaw = null;
            if (layout.HasEventDetectType)
            {
                if (!TryReadUInt32(br, out uint dt)) return Fail();
                eventDetectTypeRaw = dt;
                if (dt > 1000) return Fail();
            }

            if (!TryReadInt32(br, out int segCount)) return Fail();
            if (segCount < 0 || segCount > 200000) return Fail();

            int ai = 0, tr = 0, unk = 0;
            if (layout.HasCounts)
            {
                if (!TryReadInt32(br, out ai)) return Fail();
                if (!TryReadInt32(br, out tr)) return Fail();
                if (!TryReadInt32(br, out unk)) return Fail();
                if (ai < 0 || tr < 0 || unk < 0) return Fail();
                // [NAIMS-ISSUE:SPSLIST-COUNTS-OVERFLOW] AI/TR/UNK 는 "표시용" 카운터라 SegmentCount 를 넘을 수 있다.
                //   두 레거시 모두 파일에서 읽은 카운터 값 위에 세그먼트를 세며 ++ 를 덧쌓고 그대로 다시 저장한다
                //   (AAS ModelPreciseAnalysis.cpp:4532 / USPASK ShortSignalProcessMainDlg.cpp:1526 의 읽기 루프)
                //   → 열었다 저장할 때마다 값이 누적된다. 실측: 수집원음자료\ISUS83_20241125132026 의 SPSlist 는
                //     TR=2 인데 세그먼트는 1개(같은 파일의 단주기원음자료 사본은 TR=1) — 정상 AAS 파일이다.
                //   예전 게이트(ai/tr/unk <= segCount)는 이 파일을 통째로 거부해 항목 전체가 이관 실패했다.
                //   실제 개수는 ParseWith 에서 세그먼트를 직접 세어 다시 채우므로 여기선 상한만 본다.
                if (ai > 200000 || tr > 200000 || unk > 200000) return Fail();
            }

            if (!TryReadUInt32(br, out uint manualAuto)) return Fail();

            string dirPath = string.Empty;
            if (layout.DirLen > 0)
            {
                if (!TryReadBytesExact(br, layout.DirLen, out var dirBuf)) return Fail();
                dirPath = DecodeFixedString(dirBuf, Encoding.GetEncoding(949));
            }
            // 구조체 통째 읽기면 헤더 끝에 꼬리 정렬 패딩이 붙는다(크기는 빌드 환경 의존 → 후보로 시도).
            if (layout.EvtTrailPad > 0 && !TrySkip(br, layout.EvtTrailPad)) return Fail();

            long remain = fs.Length - fs.Position;
            long need = (long)segCount * layout.SegSize;
            if (remain < need) return Fail();

            segmentCount = segCount;
            ev.EventID = eventId;
            ev.StartTime = startTime;
            ev.EndTime = endTime;
            ev.EventLength = eventLength;
            ev.SegmentCount = segCount;
            ev.ManualOrAuto = manualAuto != 0;
            ev.DirPath = dirPath;

            if (eventDetectTypeRaw.HasValue)
            {
                ev.DetectingType = eventDetectTypeRaw.Value switch
                {
                    1 => "AI",
                    2 => "TR",
                    _ => "UNKOWN"
                };
            }
            return true;
        }

        /// <summary>
        /// 현재 스트림 위치에서 세그먼트 레코드 1건(고정 <see cref="SegmentRecordSize"/>=64바이트)을 파싱한다.
        /// 필드 레이아웃(리틀엔디언, 오프셋 누계):
        ///   +0  SegmentID(UInt32,4B)
        ///   +4  StartTime(Int64,8B), +12 EndTime(Int64,8B), +20 Length(Int64,8B)
        ///   +28 LDAx(Single,4B), +32 LDAy(Single,4B)  — LDA(선형판별분석) 좌표
        ///   +36 SegType(UInt32,4B)  — 검출 타입(1=AI, 2=TR)
        ///   +40 FrameCount(UInt32,4B)
        ///   +44 StartFreq(Single,4B), +48 EndFreq(Single,4B)
        ///   +52 Modulation(Int32,4B)
        ///   +56 CenterFreq(Single,4B), +60 BandWidth(Single,4B)  → 합계 64바이트.
        /// 어느 한 필드라도 잔여 바이트 부족으로 읽지 못하면 즉시 false로 중단한다.
        /// </summary>
        /// <param name="br">CP949로 구성된 BinaryReader.</param>
        /// <param name="seg">파싱에 성공하면 채워진 세그먼트. 실패 시 부분/빈 인스턴스.</param>
        /// <returns>64바이트 전 필드를 온전히 읽으면 true, 중간에 바이트가 모자라면 false.</returns>
        private static bool TryReadSegment(BinaryReader br, HeaderLayout layout, out SdSegment seg)
        {
            seg = new SdSegment();
            long start = br.BaseStream.Position;
            if (!TryReadUInt32(br, out uint segId)) return false;
            // 구조체 통째 읽기면 UINT(4B) 뒤 __int64 앞에 4B 정렬 패딩이 있다.
            if (layout.Padded && !TrySkip(br, 4)) return false;
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

            // 레코드 전체 크기(패딩 포함)만큼 정확히 소비해 다음 레코드 경계를 맞춘다.
            long consumed = br.BaseStream.Position - start;
            if (consumed < layout.SegSize && !TrySkip(br, (int)(layout.SegSize - consumed))) return false;
            return true;
        }

        /// <summary>정렬 패딩 등 의미 없는 n바이트를 건너뛴다. 잔여가 모자라면 false(잘린 파일 방어).</summary>
        /// <param name="br">읽을 BinaryReader.</param>
        /// <param name="n">건너뛸 바이트 수.</param>
        /// <returns>정확히 n바이트를 건너뛰면 true.</returns>
        private static bool TrySkip(BinaryReader br, int n)
        {
            if (br.BaseStream.Length - br.BaseStream.Position < n) return false;
            br.BaseStream.Position += n;
            return true;
        }

        /// <summary>
        /// 파싱된 세그먼트가 물리적으로 그럴듯한지 휴리스틱으로 검사한다(레이아웃 오정렬 탐지용 sanity check).
        /// 판정 기준: SegmentID 범위(0~10억), StartTime/EndTime 시간 역전(둘 다 양수인데 End&lt;Start),
        /// Length 음수, LDA/주파수/센터/대역폭 값의 유한성(NaN·Inf 배제) 및 음수·상한(주파수류 100만) 초과,
        /// FrameCount 범위(0~1000만), DetectingType 범위(0~1000)를 모두 만족해야 한다.
        /// 하나라도 벗어나면 잘못된 레이아웃으로 읽었을 가능성이 높다고 보고 false를 반환한다.
        /// </summary>
        /// <param name="s">검사 대상 세그먼트.</param>
        /// <returns>모든 범위 조건을 통과하면 true(유효해 보임), 아니면 false.</returns>
        private static bool LooksLikeValidSegment(SdSegment s)
        {
            if (s.SegmentID < 0 || s.SegmentID > 1_000_000_000) return false;
            if (s.StartTime > 0 && s.EndTime > 0 && s.EndTime < s.StartTime) return false;
            if (s.Length < 0) return false;
            if (!double.IsFinite(s.LDAx) || !double.IsFinite(s.LDAy)) return false;
            if (!double.IsFinite(s.StartFreq) || !double.IsFinite(s.EndFreq)) return false;
            if (s.StartFreq < 0 || s.EndFreq < 0) return false;
            if (s.StartFreq > 1_000_000 || s.EndFreq > 1_000_000) return false;
            if (!double.IsFinite(s.CenterFreq) || !double.IsFinite(s.BandWidth)) return false;
            if (s.CenterFreq < 0 || s.CenterFreq > 1_000_000) return false;
            if (s.BandWidth < 0 || s.BandWidth > 1_000_000) return false;
            if (s.FrameCount < 0 || s.FrameCount > 10_000_000) return false;
            if (s.DetectingType < 0 || s.DetectingType > 1000) return false;
            return true;
        }

        /// <summary>
        /// 고정폭 바이트 버퍼를 문자열로 디코드한다. C/C++ 고정 배열 문자열이므로
        /// 지정 인코딩(CP949)으로 변환한 뒤 첫 NUL('\0')에서 잘라 뒤쪽 패딩을 제거하고 공백을 Trim한다.
        /// </summary>
        /// <param name="bytes">고정폭 필드에서 읽은 원시 바이트(NUL 종단 + 잔여 패딩 포함 가능).</param>
        /// <param name="enc">디코딩에 사용할 인코딩(레거시 한글 경로 처리를 위해 CP949).</param>
        /// <returns>NUL 이전까지 디코드하고 공백을 정리한 문자열.</returns>
        private static string DecodeFixedString(byte[] bytes, Encoding enc)
        {
            var s = enc.GetString(bytes);
            int nul = s.IndexOf('\0');
            if (nul >= 0) s = s[..nul];
            return s.Trim();
        }

        /// <summary>지정 길이만큼 정확히 읽어낸다. 잔여 바이트가 부족해 요청 길이보다 적게 읽히면 false(잘린 파일 방어).</summary>
        /// <param name="br">읽을 BinaryReader.</param>
        /// <param name="len">읽어야 할 바이트 수.</param>
        /// <param name="bytes">읽은 바이트 배열(길이가 len과 다를 수 있음).</param>
        /// <returns>정확히 len바이트를 읽으면 true.</returns>
        private static bool TryReadBytesExact(BinaryReader br, int len, out byte[] bytes)
        { bytes = br.ReadBytes(len); return bytes.Length == len; }
        /// <summary>4바이트 Int32를 경계 검사 후 읽는다. 잔여 바이트가 4 미만이면 읽지 않고 false.</summary>
        /// <param name="br">읽을 BinaryReader.</param>
        /// <param name="v">읽은 값(실패 시 0).</param>
        /// <returns>읽기 성공 시 true.</returns>
        private static bool TryReadInt32(BinaryReader br, out int v)
        { v = 0; if (br.BaseStream.Length - br.BaseStream.Position < 4) return false; v = br.ReadInt32(); return true; }
        /// <summary>4바이트 UInt32를 경계 검사 후 읽는다. 잔여 바이트가 4 미만이면 읽지 않고 false.</summary>
        /// <param name="br">읽을 BinaryReader.</param>
        /// <param name="v">읽은 값(실패 시 0).</param>
        /// <returns>읽기 성공 시 true.</returns>
        private static bool TryReadUInt32(BinaryReader br, out uint v)
        { v = 0; if (br.BaseStream.Length - br.BaseStream.Position < 4) return false; v = br.ReadUInt32(); return true; }
        /// <summary>8바이트 Int64를 경계 검사 후 읽는다. 잔여 바이트가 8 미만이면 읽지 않고 false.</summary>
        /// <param name="br">읽을 BinaryReader.</param>
        /// <param name="v">읽은 값(실패 시 0).</param>
        /// <returns>읽기 성공 시 true.</returns>
        private static bool TryReadInt64(BinaryReader br, out long v)
        { v = 0; if (br.BaseStream.Length - br.BaseStream.Position < 8) return false; v = br.ReadInt64(); return true; }
        /// <summary>4바이트 IEEE754 Single을 경계 검사 후 읽는다. 잔여 바이트가 4 미만이면 읽지 않고 false.</summary>
        /// <param name="br">읽을 BinaryReader.</param>
        /// <param name="v">읽은 값(실패 시 0).</param>
        /// <returns>읽기 성공 시 true.</returns>
        private static bool TryReadSingle(BinaryReader br, out float v)
        { v = 0; if (br.BaseStream.Length - br.BaseStream.Position < 4) return false; v = br.ReadSingle(); return true; }
    }
}
