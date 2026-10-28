using System.Collections.ObjectModel;

namespace RawCollectMigrator.Features.ExtractRaw
{
    /// <summary>
    /// 단주기 이벤트/세그먼트 모델 (열람 전용, Main 프로젝트 SdLoader.Models 서브셋).
    /// 파싱된 SPSlist/이벤트리스트를 그리드에 뿌리는 목적.
    /// </summary>
    public sealed class SdEvent
    {
        /// <summary>그리드 표시용 순번(0-base). 원본 리스트 내 행 위치.</summary>
        public int Index { get; set; }
        /// <summary>이벤트 고유 ID. 레거시 이벤트리스트의 EventID 필드.</summary>
        public long EventID { get; set; }
        /// <summary>이벤트 시작 시각. 원본 저장 단위는 datetime tick 또는 us(마이크로초).</summary>
        public long StartTime { get; set; }
        /// <summary>이벤트 종료 시각. 저장 단위는 <see cref="StartTime"/>과 동일.</summary>
        public long EndTime { get; set; }
        /// <summary>이벤트 길이(raw). FrameCount = Length / 15625 규약으로 초 환산.</summary>
        public long EventLength { get; set; }
        /// <summary>탐지 종류 코드(원본 문자열 그대로). AI/TR/UNK 등 분류 구분.</summary>
        public string DetectingType { get; set; } = "";
        /// <summary>펄스 반복 주기(Pulse Repetition Interval).</summary>
        public long PRI { get; set; }
        /// <summary>TRM(송신) 모드 문자열.</summary>
        public string TRMMode { get; set; } = "";
        /// <summary>음압 레벨(Sound Pressure Level, dB).</summary>
        public double SPL { get; set; }
        /// <summary>변조 방식 문자열(CW/FM 등).</summary>
        public string Modulation { get; set; } = "";
        /// <summary>분석자 코멘트.</summary>
        public string Comment { get; set; } = "";
        /// <summary>그램(gram) 스니펫 이미지 파일명.</summary>
        public string GramSnippetFileName { get; set; } = "";
        /// <summary>오디오 스니펫 파일명.</summary>
        public string AudioSnippetFileName { get; set; } = "";
        /// <summary>스니펫에 대한 코멘트.</summary>
        public string SnippetComment { get; set; } = "";
        /// <summary>수동/자동 탐지 여부. true=수동, false=자동.</summary>
        public bool ManualOrAuto { get; set; }
        /// <summary>이벤트 관련 원음/스니펫이 위치한 디렉터리 경로.</summary>
        public string DirPath { get; set; } = "";

        /// <summary>이벤트에 속한 전체 세그먼트 수.</summary>
        public int SegmentCount { get; set; }
        /// <summary>세그먼트 중 AI(자동식별) 유형 개수.</summary>
        public int AiSegmentCount { get; set; }
        /// <summary>세그먼트 중 TR(추적) 유형 개수.</summary>
        public int TrSegmentCount { get; set; }
        /// <summary>세그먼트 중 미분류(UNK) 개수.</summary>
        public int UnkSegmentCount { get; set; }

        /// <summary>이벤트에 속한 세그먼트 목록(그리드 하위 바인딩용).</summary>
        public ObservableCollection<SdSegment> Segments { get; } = new();

        /// <summary>시작 시각 표시 문자열(<see cref="FormatTime"/> 적용).</summary>
        public string StartTimeText => FormatTime(StartTime);
        /// <summary>종료 시각 표시 문자열.</summary>
        public string EndTimeText => FormatTime(EndTime);
        /// <summary>길이 표시 문자열(초 환산 또는 raw).</summary>
        public string LengthText => FormatDuration(EventLength);
        /// <summary>세그먼트 수 요약 문자열(총계 및 AI/TR/UNK 분해).</summary>
        public string SegCountText =>
            SegmentCount == 0 ? "0" : $"{SegmentCount} (AI {AiSegmentCount}/TR {TrSegmentCount}/UNK {UnkSegmentCount})";

        // 마이크로초(us) 또는 100ns 단위로 저장된 시각을 대략 표시
        private static string FormatTime(long t)
        {
            if (t <= 0) return "-";
            // 값 크기로 단위 추정 — long 이지만 실제 도메인은 datetime tick 또는 us
            try
            {
                if (t > 621355968000000000L && t < 3155378975999999999L)
                    return new DateTime(t, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss.fff");
            }
            catch { }
            return t.ToString("N0");
        }
        private static string FormatDuration(long len)
        {
            if (len <= 0) return "-";
            double sec = len / 15625.0;   // 원본 파서 규약: FrameCount = Length / 15625
            return sec > 0.001 ? $"{sec:F3} s" : $"{len} raw";
        }
    }

    /// <summary>
    /// 이벤트 하위 세그먼트 모델(열람 전용). 원본 SPSlist의 개별 세그먼트 행에 대응.
    /// 주파수/변조/탐지유형 등 세그먼트 단위 특성을 그리드에 표시.
    /// </summary>
    public sealed class SdSegment
    {
        /// <summary>그리드 표시용 순번(0-base).</summary>
        public int Index { get; set; }
        /// <summary>세그먼트 고유 ID. 원본 SPSlist의 SegmentID.</summary>
        public int SegmentID { get; set; }
        /// <summary>세그먼트 시작 시각(저장 단위는 이벤트와 동일).</summary>
        public long StartTime { get; set; }
        /// <summary>세그먼트 종료 시각.</summary>
        public long EndTime { get; set; }
        /// <summary>세그먼트 길이(raw). FrameCount = Length / 15625 규약.</summary>
        public long Length { get; set; }
        /// <summary>LDA(Line Detection Array) 좌표 X 성분.</summary>
        public double LDAx { get; set; }
        /// <summary>LDA 좌표 Y 성분.</summary>
        public double LDAy { get; set; }
        /// <summary>탐지 유형 코드값. 1=AI, 2=TR, 그 외=원본 코드(<see cref="DetTypeText"/> 참조).</summary>
        public int DetectingType { get; set; }
        /// <summary>세그먼트 프레임 수(Length / 15625).</summary>
        public int FrameCount { get; set; }
        /// <summary>시작 주파수(Hz).</summary>
        public double StartFreq { get; set; }
        /// <summary>종료 주파수(Hz).</summary>
        public double EndFreq { get; set; }
        /// <summary>변조 방식 코드값.</summary>
        public int Modulation { get; set; }
        /// <summary>중심 주파수(Hz).</summary>
        public double CenterFreq { get; set; }
        /// <summary>대역폭(Hz).</summary>
        public double BandWidth { get; set; }
        /// <summary>세그먼트 스니펫 파일명.</summary>
        public string SnippetFileName { get; set; } = "";
        /// <summary>DB에 적재된 세그먼트 ID(이관 후 매핑 키).</summary>
        public long DBSegID { get; set; }
        /// <summary>패턴 매칭 값(식별 신뢰도/유사도 지표).</summary>
        public double PatternValue { get; set; }

        /// <summary>탐지 유형 표시 문자열. 코드값 1→AI, 2→TR, 그 외 원본 값.</summary>
        public string DetTypeText => DetectingType switch
        {
            1 => "AI",
            2 => "TR",
            _ => DetectingType.ToString()
        };
        /// <summary>시작 주파수 표시 문자열(소수 1자리).</summary>
        public string StartFreqText => $"{StartFreq:F1}";
        /// <summary>종료 주파수 표시 문자열(소수 1자리).</summary>
        public string EndFreqText   => $"{EndFreq:F1}";
        /// <summary>중심 주파수 표시 문자열(소수 1자리).</summary>
        public string CenterFreqText => $"{CenterFreq:F1}";
        /// <summary>대역폭 표시 문자열(소수 1자리).</summary>
        public string BandWidthText  => $"{BandWidth:F1}";
    }

    /// <summary>
    /// 리스트 파일(.lst) 헤더 모델. 원음자료 1건의 메타데이터(탐지 체계/함명/일자 등)를 표현.
    /// 레거시 LST 헤더 블록에 대응하며, 요약 표시용으로 사용.
    /// </summary>
    public sealed class LstHeader
    {
        /// <summary>원음(원 음향자료) 파일명.</summary>
        public string RawAcousticDataFileName { get; set; } = "";
        /// <summary>탐지 체계명.</summary>
        public string DetectSystem { get; set; } = "";
        /// <summary>탐지 함명.</summary>
        public string DetectShipName { get; set; } = "";
        /// <summary>탐지 소나명.</summary>
        public string DetectSonar { get; set; } = "";
        /// <summary>탐지 일자.</summary>
        public string DetectDate { get; set; } = "";
        /// <summary>분석자 ID.</summary>
        public string AnalyzerID { get; set; } = "";
        /// <summary>분류(식별) 장비명.</summary>
        public string ClassificationEquipment { get; set; } = "";

        /// <summary>헤더 항목을 여러 줄로 정리한 요약 표시 문자열.</summary>
        public string Summary =>
            $"원음자료명 : {RawAcousticDataFileName}\n" +
            $"탐지체계   : {DetectSystem}\n" +
            $"탐지함명   : {DetectShipName}\n" +
            $"탐지소나   : {DetectSonar}\n" +
            $"탐지일자   : {DetectDate}\n" +
            $"분석자ID   : {AnalyzerID}\n" +
            $"분류장비   : {ClassificationEquipment}";
    }
}
