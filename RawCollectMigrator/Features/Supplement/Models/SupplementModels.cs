namespace RawCollectMigrator.Features.Supplement
{
    /// <summary>
    /// 보조정보.alg 전체 스키마. Main 프로젝트 Features/Supplement/SupplementModels.cs 이식.
    /// </summary>
    public sealed class SupplementSheet
    {
        /// <summary>임무정보 헤더(진수체계/진수함정/임무일자/해역 등). RECORDING_* 컬럼의 원천.</summary>
        public SupplementMissionInfo MissionInfo { get; set; } = new();
        /// <summary>수록매체(Recmedia) 목록. 각 매체는 여러 레코드 구간을 포함.</summary>
        public List<SupplementRecmedia> Recmedia { get; } = new();

        /// <summary>세트(관측 회차) 개수. 아래 정보 리스트들의 SetIndex 범위와 대응.</summary>
        public int SetCount { get; set; }

        /// <summary>세트별 항해·해양환경 정보(위치/심도/수온 등).</summary>
        public List<SupplementNaviSeaInfo> NaviSeaInfos { get; } = new();
        /// <summary>세트별 소음원(자함 기계 소음) 정보.</summary>
        public List<SupplementNoiseInfo> NoiseInfos { get; } = new();
        /// <summary>세트별 표적 정보.</summary>
        public List<SupplementTargetInfo> TargetInfos { get; } = new();
        /// <summary>세트별 협대역(Narrow Band) 탐지 정보.</summary>
        public List<SupplementNbDetectionInfo> NbDetectionInfos { get; } = new();
    }

    /// <summary>
    /// 보조정보.alg 임무정보 헤더 블록. 진수(수록) 체계·함정과 임무 일자/해역을 담으며
    /// 신규 DB의 RECORDING_SYSTEM / RECORDING_VESSEL / RECORDING_DATETIME 등에 대응.
    /// </summary>
    public sealed class SupplementMissionInfo
    {
        /// <summary>진수 플랫폼 구분(수상함/잠수함 등).</summary>
        public string DetPlatform { get; set; } = "";
        /// <summary>진수 함정 함급/체계명. RECORDING_SYSTEM 매핑의 입력값(RecordingSystemMapped 참조).</summary>
        public string DetVesselClass { get; set; } = "";
        /// <summary>진수 함정명. RECORDING_VESSEL에 대응.</summary>
        public string DetVesselName { get; set; } = "";
        /// <summary>임무 시작 일자. RECORDING_DATETIME(시작) 원천.</summary>
        public string MissionDateFr { get; set; } = "";
        /// <summary>임무 종료 일자.</summary>
        public string MissionDateTo { get; set; } = "";
        /// <summary>임무 해역명.</summary>
        public string MissionSeaArea { get; set; } = "";
        /// <summary>임무 개요.</summary>
        public string MissionSummary { get; set; } = "";
        /// <summary>임무 관련 비고/특이사항.</summary>
        public string MissionComment { get; set; } = "";

        /// <summary>DetVesselClass → 신규 RECORDING_SYSTEM 코드 매핑.</summary>
        public string RecordingSystemMapped
        {
            get
            {
                var s = DetVesselClass.Trim().ToUpperInvariant();
                return s switch
                {
                    "ISUS83-TCIS" or "ISUS83TCIS" or "ISUSTCIS" or "ISUS83" => "ISUS83-TCIS",
                    "ISUS90" => "ISUS90",
                    "JANGBOGO3" => "JANGBOGO3",
                    "ADD" => "ADD",
                    "US" or "USNAVY" => "US-NAVY",
                    "" => "ETC",
                    _ => "ETC"
                };
            }
        }
    }

    /// <summary>수록매체 1건. 하나의 매체에 담긴 레코드(수록 구간) 집합을 표현.</summary>
    public sealed class SupplementRecmedia
    {
        /// <summary>매체 순번(0-base). 레코드의 RecmediaIndex와 연결.</summary>
        public int Index { get; set; }
        /// <summary>이 매체에 속한 레코드 개수.</summary>
        public int RecordCount { get; set; }
        /// <summary>매체에 담긴 수록 레코드 목록.</summary>
        public List<SupplementRecord> Records { get; } = new();
    }

    /// <summary>수록매체 내 개별 레코드(수록 시간 구간).</summary>
    public sealed class SupplementRecord
    {
        /// <summary>상위 수록매체 순번(SupplementRecmedia.Index 참조).</summary>
        public int RecmediaIndex { get; set; }
        /// <summary>매체 내 레코드 순번.</summary>
        public int Index { get; set; }
        /// <summary>수록 시작 시각.</summary>
        public string RecordTimeFr { get; set; } = "";
        /// <summary>수록 종료 시각.</summary>
        public string RecordTimeTo { get; set; } = "";
        /// <summary>레코드 비고.</summary>
        public string RecordComment { get; set; } = "";
    }

    /// <summary>세트(관측 회차)별 항해·해양환경 정보. 진수 위치·심도·속력과 수온 프로파일을 담음.</summary>
    public sealed class SupplementNaviSeaInfo
    {
        /// <summary>소속 세트 번호(SupplementSheet.SetCount 범위 내).</summary>
        public int SetIndex { get; set; }
        /// <summary>관측 일자.</summary>
        public string DetDate { get; set; } = "";
        /// <summary>관측 시각.</summary>
        public string DetTime { get; set; } = "";
        /// <summary>자함 심도(m).</summary>
        public int Depth { get; set; }
        /// <summary>예인배열(TA, Towed Array) 심도(m).</summary>
        public int TADepth { get; set; }
        /// <summary>위도.</summary>
        public double Latitude { get; set; }
        /// <summary>경도.</summary>
        public double Longitude { get; set; }
        /// <summary>진수 침로(항로).</summary>
        public string DetPath { get; set; } = "";
        /// <summary>자함 속력(kts).</summary>
        public double Speed { get; set; }
        /// <summary>해상 상황/기상.</summary>
        public string Situation { get; set; } = "";
        /// <summary>파고(m).</summary>
        public double WaveHeight { get; set; }
        /// <summary>해역 수심(m).</summary>
        public double RgnDepth { get; set; }

        /// <summary>수온 프로파일 측정점 개수.</summary>
        public int TemperatureCount { get; set; }
        /// <summary>심도별 수온·음속 프로파일 목록.</summary>
        public List<SupplementTemperature> Temperatures { get; } = new();
    }

    /// <summary>수온 프로파일 측정점 1건(심도별 수온·음속·염도). SupplementNaviSeaInfo에 종속.</summary>
    public sealed class SupplementTemperature
    {
        /// <summary>소속 세트 번호.</summary>
        public int SetIndex { get; set; }
        /// <summary>측정점 순번.</summary>
        public int Index { get; set; }
        /// <summary>측정 심도(m).</summary>
        public double Depth { get; set; }
        /// <summary>수온(℃).</summary>
        public double Temperature { get; set; }
        /// <summary>음속(m/s).</summary>
        public double Sonic { get; set; }
        /// <summary>염분(psu).</summary>
        public double Salt { get; set; }
        /// <summary>수온약층(층심도) 심도(m).</summary>
        public double GradeDepth { get; set; }
        /// <summary>측정점 비고.</summary>
        public string Comment { get; set; } = "";
    }

    /// <summary>세트별 소음원 정보. 관측 중 가동된 자함 기계류 소음 목록을 담음.</summary>
    public sealed class SupplementNoiseInfo
    {
        /// <summary>소속 세트 번호.</summary>
        public int SetIndex { get; set; }
        /// <summary>기계(소음원) 개수.</summary>
        public int MachineCount { get; set; }
        /// <summary>소음원 기계 목록.</summary>
        public List<SupplementMachine> Machines { get; } = new();
    }

    /// <summary>소음원 기계 1건(가동 시간·발생 주파수). SupplementNoiseInfo에 종속.</summary>
    public sealed class SupplementMachine
    {
        /// <summary>소속 세트 번호.</summary>
        public int SetIndex { get; set; }
        /// <summary>기계 순번.</summary>
        public int Index { get; set; }
        /// <summary>기계명.</summary>
        public string MachineName { get; set; } = "";
        /// <summary>가동 시작 시각.</summary>
        public string RunTimeFr { get; set; } = "";
        /// <summary>가동 종료 시각.</summary>
        public string RunTimeTo { get; set; } = "";
        /// <summary>발생(특성) 주파수(Hz).</summary>
        public double OccurenceFreq { get; set; }
        /// <summary>기계 비고.</summary>
        public string Comment { get; set; } = "";
    }

    /// <summary>세트별 표적 정보. 관측된 표적(Contact) 목록을 담음.</summary>
    public sealed class SupplementTargetInfo
    {
        /// <summary>소속 세트 번호.</summary>
        public int SetIndex { get; set; }
        /// <summary>표적 개수.</summary>
        public int TargetCount { get; set; }
        /// <summary>표적 목록.</summary>
        public List<SupplementTarget> Targets { get; } = new();
    }

    /// <summary>
    /// 표적(Contact) 1건. 수록 조건(소나·감도·전치증폭·AGC 등)과 표적 식별
    /// (국적/종류/함급/함명)·기동 정보를 담음. SupplementTargetInfo에 종속.
    /// </summary>
    public sealed class SupplementTarget
    {
        /// <summary>소속 세트 번호.</summary>
        public int SetIndex { get; set; }
        /// <summary>표적 번호. 협대역 탐지(SupplementNbDetection.TargetNumber)와 연결.</summary>
        public int TargetNumber { get; set; }
        /// <summary>접촉(수록) 시작 시각.</summary>
        public string ContactRecTimeFr { get; set; } = "";
        /// <summary>접촉(수록) 종료 시각.</summary>
        public string ContactRecTimeTo { get; set; } = "";
        /// <summary>식별(분류) 방법.</summary>
        public string ClassifyMethod { get; set; } = "";
        /// <summary>탐지 소나.</summary>
        public string DetSonar { get; set; } = "";
        /// <summary>감도 설정.</summary>
        public string Sensitivity { get; set; } = "";
        /// <summary>전치증폭기(Preamplifier) 설정.</summary>
        public string Preamplifier { get; set; } = "";
        /// <summary>자동이득제어(AGC) 설정.</summary>
        public string AGC { get; set; } = "";
        /// <summary>수록매체 번호.</summary>
        public string RecmediaNumber { get; set; } = "";
        /// <summary>선미/선수(AFT/FWD) 구분.</summary>
        public string AFTFWD { get; set; } = "";
        /// <summary>추적 채널.</summary>
        public string TrackChannel { get; set; } = "";
        /// <summary>원신호(Raw signal) 번호.</summary>
        public string RawsignalNumber { get; set; } = "";
        /// <summary>원신호 파일/이름.</summary>
        public string RawsignalName { get; set; } = "";
        /// <summary>주파수 대역(Hz).</summary>
        public double FrequencyBand { get; set; }
        /// <summary>표본화율(sample rate, Hz).</summary>
        public double SampleRate { get; set; }
        /// <summary>표적 국적.</summary>
        public string Nation { get; set; } = "";
        /// <summary>표적 종류(수상함/잠수함 등).</summary>
        public string TargetType { get; set; } = "";
        /// <summary>표적 함급.</summary>
        public string TargetClass { get; set; } = "";
        /// <summary>표적 함명.</summary>
        public string TargetName { get; set; } = "";
        /// <summary>표적 기동 모드.</summary>
        public string NaviMode { get; set; } = "";
        /// <summary>표적 거리(m 또는 yd).</summary>
        public double TargetDistance { get; set; }
        /// <summary>표적 속력(kts).</summary>
        public double TargetSpeed { get; set; }
        /// <summary>기관(엔진) 형식.</summary>
        public string EngineType { get; set; } = "";
        /// <summary>탐지 신호 형식.</summary>
        public string DetsignalType { get; set; } = "";
    }

    /// <summary>세트별 협대역(Narrow Band) 탐지 정보. 표적별 협대역 분석 결과 목록을 담음.</summary>
    public sealed class SupplementNbDetectionInfo
    {
        /// <summary>소속 세트 번호.</summary>
        public int SetIndex { get; set; }
        /// <summary>협대역 탐지 항목 개수.</summary>
        public int NbDetinfoCount { get; set; }
        /// <summary>협대역 탐지 목록.</summary>
        public List<SupplementNbDetection> NbDetections { get; } = new();
    }

    /// <summary>
    /// 협대역 탐지 1건. 표적의 협대역 음향 특성(축·기관 관련 주파수비, RPM 등)과
    /// 분석 결과를 담음. SupplementNbDetectionInfo에 종속.
    /// </summary>
    public sealed class SupplementNbDetection
    {
        /// <summary>소속 세트 번호.</summary>
        public int SetIndex { get; set; }
        /// <summary>표적 번호(SupplementTarget.TargetNumber와 연결).</summary>
        public int TargetNumber { get; set; }
        /// <summary>탐지 시작 시각.</summary>
        public string DetTimeFr { get; set; } = "";
        /// <summary>탐지 종료 시각.</summary>
        public string DetTimeTo { get; set; } = "";
        /// <summary>진수(수록) 함정 함급.</summary>
        public string DetVesselClass { get; set; } = "";
        /// <summary>진수(수록) 함정명.</summary>
        public string DetVesselName { get; set; } = "";
        /// <summary>표적 식별 결과.</summary>
        public string TargetClassify { get; set; } = "";
        /// <summary>표적 거리.</summary>
        public double Distance { get; set; }
        /// <summary>표적 속력.</summary>
        public double Speed { get; set; }
        /// <summary>분석 결과 파일 경로/이름.</summary>
        public string AnalysisResultFile { get; set; } = "";

        /// <summary>특성 주파수(Hz).</summary>
        public double FeatureFreq { get; set; }
        /// <summary>우세(주요) 주파수(Hz).</summary>
        public double SuperiorFreq { get; set; }
        /// <summary>실린더 수.</summary>
        public int CylinderCount { get; set; }
        /// <summary>날개(Blade) 관련 주파수/비(BR).</summary>
        public double BR { get; set; }
        /// <summary>축 회전 관련 주파수/비(PSR).</summary>
        public double PSR { get; set; }
        /// <summary>축 회전수(PSRPM, RPM).</summary>
        public double PSRPM { get; set; }
        /// <summary>TSR 주파수/비.</summary>
        public double TSR { get; set; }
        /// <summary>CFR 주파수/비.</summary>
        public double CFR { get; set; }
        /// <summary>CSR 주파수/비.</summary>
        public double CSR { get; set; }
        /// <summary>기관(엔진) 관련 주파수/비(EFR).</summary>
        public double EFR { get; set; }
        /// <summary>기관 회전수(ERPM, RPM).</summary>
        public double ERPM { get; set; }
        /// <summary>관련 기계(소음원)명.</summary>
        public string Machine { get; set; } = "";
        /// <summary>SWATH 관련 값.</summary>
        public double SWATH { get; set; }
        /// <summary>특이 신호 여부/설명.</summary>
        public string OddSignal { get; set; } = "";
        /// <summary>LP 관련 값.</summary>
        public double LP { get; set; }
        /// <summary>피치(Pitch) 값.</summary>
        public double Pitch { get; set; }
        /// <summary>탐지 방위(좌/우현) 구분.</summary>
        public string DetSide { get; set; } = "";
        /// <summary>탐지 비고.</summary>
        public string Comment { get; set; } = "";

        /// <summary>협대역 지표(라인) 개수.</summary>
        public int IndexCount { get; set; }
        /// <summary>협대역 주파수 라인(지표) 목록.</summary>
        public List<SupplementNbIndex> Indices { get; } = new();
    }

    /// <summary>협대역 주파수 라인(지표) 1건. 중심 주파수·정규화 주파수·차수 등. SupplementNbDetection에 종속.</summary>
    public sealed class SupplementNbIndex
    {
        /// <summary>소속 세트 번호.</summary>
        public int SetIndex { get; set; }
        /// <summary>표적 번호(SupplementNbDetection.TargetNumber와 연결).</summary>
        public int TargetNumber { get; set; }
        /// <summary>지표(라인) 순번.</summary>
        public int Index { get; set; }
        /// <summary>중심 주파수(Hz).</summary>
        public double CenterFreq { get; set; }
        /// <summary>정규화 주파수(Hz).</summary>
        public double NormalFreq { get; set; }
        /// <summary>하모닉(고조파) 차수.</summary>
        public int Harmonic { get; set; }
        /// <summary>소음원(발생원) 구분.</summary>
        public string NoiseSrc { get; set; } = "";
    }
}
