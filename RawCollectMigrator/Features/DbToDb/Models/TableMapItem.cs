using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Runtime.CompilerServices;

namespace RawCollectMigrator.Features.DbToDb
{
    /// <summary>
    /// 테이블이 참조하는 외부 파일 종류.
    /// 실제 파일은 CLASSIFICATIONDB 루트 아래 종류별 하위 폴더에 있다(2026-07-21 실측).
    /// </summary>
    public enum SnippetFileKind
    {
        None,
        GramAndWav,   // .grm + .wav  → SNIPPET
        WavOnly,      // .wav 만       → SNIPPET
        Image,        // 함정 영상자료 → IMAGE
        CountryFlag   // 국기 이미지   → CTRYIMAGE
    }

    /// <summary>
    /// 소스 DB(구 NAIMS = 기준식별 DB) → 타겟 DB(NAIMS2) 테이블 매핑 한 건.
    /// 확장 영역에서 컬럼 스키마 + 상위 N행 샘플을 로드해 전시한다.
    /// </summary>
    public sealed class TableMapItem : INotifyPropertyChanged
    {
        public string Category    { get; init; } = "";
        public string SourceTable { get; init; } = "";
        public string TargetTable { get; init; } = "";
        public string Note        { get; init; } = "";

        /// <summary>이 테이블이 참조하는 외부 파일 종류(파일명 컬럼만 DB에 있고 실체는 파일시스템).</summary>
        public SnippetFileKind FileKind { get; init; } = SnippetFileKind.None;

        /// <summary>파일명 컬럼 설명 (UI 전시용).</summary>
        public string FileColumns { get; init; } = "";

        public bool RequiresFiles => FileKind != SnippetFileKind.None;

        public string FileKindText => FileKind switch
        {
            SnippetFileKind.GramAndWav  => ".grm + .wav",
            SnippetFileKind.WavOnly     => ".wav",
            SnippetFileKind.Image       => "이미지",
            SnippetFileKind.CountryFlag => "국기",
            _ => "-"
        };

        /// <summary>CLASSIFICATIONDB 루트 아래에서 이 테이블의 파일이 있는 하위 폴더.</summary>
        public string FileSubFolder => FileKind switch
        {
            SnippetFileKind.GramAndWav or SnippetFileKind.WavOnly => "SNIPPET",
            SnippetFileKind.Image       => "IMAGE",
            SnippetFileKind.CountryFlag => "CTRYIMAGE",
            _ => ""
        };

        /// <summary>실제 SELECT 대상이 될 소스 테이블명 (조합/와일드카드가 아닌 단일 테이블).</summary>
        public string PrimarySourceTable => FirstToken(SourceTable);
        public string PrimaryTargetTable => FirstToken(TargetTable);

        public bool IsCountable => !PrimarySourceTable.Contains('*')
                                && !PrimarySourceTable.Contains('?')
                                && !string.IsNullOrEmpty(PrimarySourceTable);

        private static string FirstToken(string s)
        {
            var t = s.Split('/')[0].Trim();
            int sp = t.IndexOf(' ');
            if (sp > 0) t = t.Substring(0, sp);
            return t;
        }

        // ── 레코드 카운트 ──
        private long? _sourceCount;
        public long? SourceCount
        {
            get => _sourceCount;
            set { if (Set(ref _sourceCount, value)) { OnChanged(nameof(SourceCountText)); OnChanged(nameof(DeltaText)); } }
        }
        public string SourceCountText => _sourceCount?.ToString("N0") ?? (IsCountable ? "?" : "-");

        private long? _targetCount;
        public long? TargetCount
        {
            get => _targetCount;
            set { if (Set(ref _targetCount, value)) { OnChanged(nameof(TargetCountText)); OnChanged(nameof(DeltaText)); } }
        }
        public string TargetCountText => _targetCount?.ToString("N0") ?? (IsCountable ? "?" : "-");

        /// <summary>이관 진척 표시: 타겟이 소스보다 적으면 미이관 건수를 보여준다.</summary>
        public string DeltaText
        {
            get
            {
                if (_sourceCount is not { } s || _targetCount is not { } t) return "";
                if (s == 0 && t == 0) return "둘 다 비어 있음";
                if (t == 0)  return $"미이관 ({s:N0}건)";
                if (t < s)   return $"부족 {s - t:N0}건";
                if (t == s)  return "일치";
                return $"타겟이 {t - s:N0}건 많음";
            }
        }

        // ── 확장 상세 ──
        private bool _isExpanded;
        public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
        public bool DetailLoaded { get; set; }

        private bool _detailLoading;
        public bool DetailLoading { get => _detailLoading; set => Set(ref _detailLoading, value); }

        private string _detailError = "";
        public string DetailError { get => _detailError; set => Set(ref _detailError, value); }

        public ObservableCollection<ColumnInfo> Columns { get; } = new();

        /// <summary>소스 ↔ 타겟 컬럼 대응. ColumnMapper 가 규칙 기반으로 생성.</summary>
        public ObservableCollection<ColumnMapping> ColumnMappings { get; } = new();

        private string _mappingSummary = "";
        public string MappingSummary { get => _mappingSummary; set => Set(ref _mappingSummary, value); }

        private DataView? _sampleView;
        public DataView? SampleView { get => _sampleView; set => Set(ref _sampleView, value); }

        private DataView? _targetSampleView;
        public DataView? TargetSampleView { get => _targetSampleView; set => Set(ref _targetSampleView, value); }

        private string _sampleSummary = "";
        public string SampleSummary { get => _sampleSummary; set => Set(ref _sampleSummary, value); }

        /// <summary>레코드 열람 시 가져올 행 수.</summary>
        private int _rowLimit = 50;
        public int RowLimit { get => _rowLimit; set => Set(ref _rowLimit, value); }

        /// <summary>좌측 목록 표시용 라벨.</summary>
        public string ListLabel => $"{SourceTable}";
        public string ListSubLabel => IsCountable
            ? $"→ {TargetTable}"
            : $"→ {TargetTable}";

        public event PropertyChangedEventHandler? PropertyChanged;
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnChanged(name);
            return true;
        }
        private void OnChanged(string? name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class ColumnInfo
    {
        public string Name { get; init; } = "";
        public string DataType { get; init; } = "";
        public int? MaxLength { get; init; }
        public string Nullable { get; init; } = "";
        public int Ordinal { get; init; }

        public string TypeText => MaxLength.HasValue && MaxLength.Value > 0
            ? $"{DataType}({MaxLength})"
            : DataType;
    }

    /// <summary>
    /// 소스 NAIMS(기준식별 DB, 42 테이블) → 타겟 NAIMS2(143 테이블)의 STD_* 매핑.
    /// 2026-07-21 양쪽 라이브 DB 실측으로 확정.
    ///   소스: 192.168.111.39 / NAIMS
    ///   타겟: 192.168.200.205 / NAIMS2
    /// </summary>
    public static class TableMapRegistry
    {
        public static List<TableMapItem> Defaults() => new()
        {
            // ── 기준 정보 (국가/함급/함정/특성) ──
            new() { Category = "기준정보", SourceTable = "COUNTRY_CODES",   TargetTable = "STD_COUNTRY_CODES",
                    FileKind = SnippetFileKind.CountryFlag, FileColumns = "FLAG_FILENAME",
                    Note = "국가 코드. 국기 이미지는 CTRYIMAGE 폴더 — 실측 시 폴더가 비어 있었음." },
            new() { Category = "기준정보", SourceTable = "CLASSES",         TargetTable = "STD_CLASSES",
                    Note = "함급 제원. 추진/엔진/터빈 계열 축비 컬럼 다수." },
            new() { Category = "기준정보", SourceTable = "VESSEL_DATA",     TargetTable = "STD_VESSEL_DATA",
                    Note = "함정 개체. CLASS_ID / COUNTRY_ID 외래키." },
            new() { Category = "기준정보", SourceTable = "CHARACTERISTICS", TargetTable = "STD_CHARACTERISTICS",
                    Note = "함정별 음향 특성(캐비테이션·이산주파수 등)." },

            // ── 수동 신호 (시그니처/하모닉) ──
            new() { Category = "수동신호", SourceTable = "SIGNATURES", TargetTable = "STD_SIGNATURES",
                    Note = "소음원 시그니처. VESSEL_ID 외래키." },
            new() { Category = "수동신호", SourceTable = "HARMONICS",  TargetTable = "STD_HARMONICS",
                    Note = "시그니처별 하모닉 성분. SIGNATURES_ID 외래키." },

            // ── 능동 소나 (소나/펄스/세그먼트) ──
            new() { Category = "능동소나", SourceTable = "ACTIVE_SONAR",            TargetTable = "STD_ACTV_SONAR",
                    Note = "능동 소나 제원." },
            new() { Category = "능동소나", SourceTable = "PULSES",                  TargetTable = "STD_PULSES",
                    Note = "펄스 정의. 최대 7개 세그먼트 참조(FIRST~SEVENTH_SEG_ID)." },
            new() { Category = "능동소나", SourceTable = "PULSE_SEGMENTS",          TargetTable = "STD_PULSE_SEGMENTS",
                    Note = "펄스 세그먼트(주파수 범위·변조·길이)." },
            new() { Category = "능동소나", SourceTable = "PULSE_FREQUENCY_ISUS90",  TargetTable = "STD_PULSE_FREQUENCY_ISUS90",
                    Note = "ISUS90 전용 펄스 주파수 표." },
            new() { Category = "능동소나", SourceTable = "PULSE_SEG_LENGTH_ISUS90", TargetTable = "STD_PULSE_SEG_LENGTH_ISUS90",
                    Note = "ISUS90 전용 세그먼트 길이 표." },
            new() { Category = "능동소나", SourceTable = "PULSE_SEG_SWING_ISUS90",  TargetTable = "STD_PULSE_SEG_SWING_ISUS90",
                    Note = "ISUS90 전용 세그먼트 스윙 표." },

            // ── 순간소음 (Transient) ──
            new() { Category = "순간소음", SourceTable = "TRANSIENT", TargetTable = "STD_TRANSIENT",
                    Note = "순간소음 정의. CLASS_ID / VESSEL_ID 외래키." },
            new() { Category = "순간소음", SourceTable = "TR_FRAME",  TargetTable = "STD_TR_FRAME",
                    Note = "순간소음 프레임. MFB_VALUE_1~32 (32밴드 특징벡터)." },

            // ── 스니펫 (CLASSIFICATIONDB\SNIPPET 파일 동반) ──
            new() { Category = "스니펫", SourceTable = "NB_SNIPPET", TargetTable = "STD_NB_SNIPPET",
                    FileKind = SnippetFileKind.GramAndWav, FileColumns = "GRAM_FILENAME / AUDIO_FILENAME",
                    Note = "협대역 스니펫. .grm 은 R8 66B 변환 필요. 실측: 참조 44개 중 32개만 존재(12개 누락, 주로 nb_CDMU_*)." },
            new() { Category = "스니펫", SourceTable = "AI_SNIPPET", TargetTable = "STD_AI_SNIPPET",
                    FileKind = SnippetFileKind.GramAndWav, FileColumns = "GRAM_FILENAME / AUDIO_FILENAME",
                    Note = "능동식별 스니펫. .grm → STD_AI_SPECTROGRAM 적재 여부 확인 필요. 실측: 참조 6개 중 1개만 존재." },
            new() { Category = "스니펫", SourceTable = "TR_SNIPPET", TargetTable = "STD_TR_SNIPPET",
                    FileKind = SnippetFileKind.WavOnly, FileColumns = "AUDIO_FILENAME",
                    Note = "순간소음 스니펫(.grm 없음). 실측: 참조 4개 전부 파일 없음 — 확인 필요." },
            new() { Category = "스니펫", SourceTable = "SIG_SNIPPET",   TargetTable = "STD_NB_SIG_SNIPPET_LINK",
                    Note = "시그니처 ↔ 협대역 스니펫 연결 테이블." },
            new() { Category = "스니펫", SourceTable = "FRAME_SNIPPET", TargetTable = "STD_TR_FRAME_SNIPPET_LINK",
                    Note = "순간소음 프레임 ↔ 스니펫 연결 테이블." },

            // ── 영상자료 ──
            new() { Category = "영상자료", SourceTable = "IMAGE_DATA", TargetTable = "STD_REFERENCE_DATA",
                    FileKind = SnippetFileKind.Image, FileColumns = "IMG_FILENAME",
                    Note = "함정 영상자료. 예: 20070613155506_영상자료_1.png — 타겟 테이블 확정 필요." },

            // ── 무장 ──
            new() { Category = "무장", SourceTable = "WEAPON",         TargetTable = "STD_WEAPON",
                    Note = "무장 제원." },
            new() { Category = "무장", SourceTable = "WEAPON_CARRIER", TargetTable = "STD_WEAPON_CLASSES_LINK",
                    Note = "무장 ↔ 함급 연결 테이블." },

            // ── 배포 관리 ──
            new() { Category = "배포관리", SourceTable = "DIST_LIST",     TargetTable = "STD_DIST_LIST",
                    Note = "배포 목록." },
            new() { Category = "배포관리", SourceTable = "DIST_TO",       TargetTable = "STD_DIST_TO",
                    Note = "배포 대상." },
            new() { Category = "배포관리", SourceTable = "DB_DIST_GROUP", TargetTable = "STD_DB_DIST_GROUP",
                    Note = "배포 그룹." },
            new() { Category = "배포관리", SourceTable = "DB_DIST_LOG",   TargetTable = "STD_DB_DIST_LOG",
                    Note = "배포 이력." },

            // ── 변경 이력 ──
            new() { Category = "변경이력", SourceTable = "CD_CHANGE_LOG", TargetTable = "STD_CD_CHANGE_LOG",
                    Note = "기준식별 변경 이력 (최다 레코드)." },
            new() { Category = "변경이력", SourceTable = "CD_BACKUP_LOG", TargetTable = "STD_CD_BACKUP_LOG",
                    Note = "기준식별 백업 이력." },
            new() { Category = "변경이력", SourceTable = "CCF2CD_LOG",    TargetTable = "STD_CCF2CD_LOG",
                    Note = "CCF→CD 변환 이력." },

            // ── 운용자 ──
            new() { Category = "운용자", SourceTable = "OPERATOR", TargetTable = "OPERATOR",
                    Note = "운용자 계정. 타겟에 동명 테이블 존재 — 병합 정책 확인 필요." },

            // ── 미확정 (타겟 매핑 확인 필요) ──
            new() { Category = "미확정", SourceTable = "RAWDATAINFO",       TargetTable = "(확인 필요)",
                    Note = "원음 자료 정보 92건. RAW_COLLECT_SIG 계열 대응 여부 확인." },
            new() { Category = "미확정", SourceTable = "RAWSIGNALINFO",     TargetTable = "(확인 필요)",
                    Note = "원음 신호 정보 63건." },
            new() { Category = "미확정", SourceTable = "RAWDATAIMPORTINFO", TargetTable = "(확인 필요)",
                    Note = "원음 반입 이력 55건." },
            new() { Category = "미확정", SourceTable = "ONA_209",           TargetTable = "(확인 필요)",
                    Note = "용도 미상 30건." },
            new() { Category = "미확정", SourceTable = "ONA_214",           TargetTable = "(확인 필요)",
                    Note = "용도 미상 6건." },

            // ── 이관 제외 후보 ──
            new() { Category = "제외후보", SourceTable = "PASSWORD",   TargetTable = "(이관 제외)",
                    Note = "레거시 계정 정보. 신규 체계 인증과 무관 — 이관 대상 아님." },
            new() { Category = "제외후보", SourceTable = "USER_JUMIN", TargetTable = "(이관 제외)",
                    Note = "개인식별정보. 이관 대상에서 제외 권장." },
            new() { Category = "제외후보", SourceTable = "DATASET",    TargetTable = "(이관 제외)",
                    Note = "비어 있음 (0건)." },
            new() { Category = "제외후보", SourceTable = "FIELD_CONV", TargetTable = "(이관 제외)",
                    Note = "비어 있음 (0건)." },
        };
    }
}
