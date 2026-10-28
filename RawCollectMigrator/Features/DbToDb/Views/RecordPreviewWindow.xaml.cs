using System.Collections.ObjectModel;
using System.Windows;

namespace RawCollectMigrator.Features.DbToDb
{
    // [찾기:미리보기]
    /// <summary>
    /// 레코드 한 건의 바이너리 내용을 <b>소스와 타겟 각각의 규약대로</b> 읽어 나란히 그린다.
    ///
    /// 무엇을 보는가 (표에 따라 다르다)
    ///   · 국가(COUNTRY_CODES)      : 국기 이미지 — 소스는 CTRYIMAGE 파일, 타겟은 FLAG_FILE blob
    ///   · 영상자료(IMAGE_DATA)     : 이미지     — 소스는 IMAGE 파일,    타겟은 FILE_DATA blob
    ///   · 협대역/능동 스니펫       : 시계열(원음 파형) + 스니펫그램 (+ AI 는 생성 스펙트로그램)
    ///   · 순간소음 스니펫          : 시계열 + 스펙트로그램(S0)
    ///
    /// 왜 양쪽을 따로 읽는가 — blob 이 "몇 바이트 들어갔다" 는 형식이 맞다는 뜻일 뿐이다.
    /// 이 프로젝트에서 AI 그램 태그(A0/S0)와 NB 그램 규약을 잘못 잡은 적이 있는데
    /// 둘 다 INSERT 는 성공했고 길이도 그럴듯했다. 그려 봐야 안다.
    ///
    /// [NAIMS-ISSUE:PREVIEW-DIVERGED] 전시 내용을 <b>여기서 만들지 않는다</b>.
    ///   예전에는 이 창과 결과 표의 인라인 전시가 각자 자기 코드로 읽었고, 실제로 갈라졌다 —
    ///   축 값(주파수·시각)을 DB 행에서 채우는 개선이 인라인에만 들어가, 같은 레코드인데
    ///   창에서는 시간축이 프레임 번호로 나왔다. 어느 쪽이 맞는지 화면만 봐서는 알 수 없었다.
    ///   그래서 생성은 <see cref="RecordPreviewBuilder"/> 한 곳에만 두고, 창은 그리기만 한다.
    /// </summary>
    public partial class RecordPreviewWindow : Window
    {
        private readonly DbToDbOptions _opt;
        private readonly string _sourceTable, _targetTable;
        private readonly long _sourcePk, _targetId;

        private readonly ObservableCollection<RecordPreviewBuilder.PanelItem> _src = new();
        private readonly ObservableCollection<RecordPreviewBuilder.PanelItem> _tgt = new();

        public RecordPreviewWindow(DbToDbOptions opt, string sourceTable, long sourcePk,
                                   string targetTable, long targetRecordId)
        {
            InitializeComponent();
            _opt = opt;
            _sourceTable = sourceTable; _sourcePk = sourcePk;
            _targetTable = targetTable; _targetId = targetRecordId;

            SrcPanels.ItemsSource = _src;
            TgtPanels.ItemsSource = _tgt;
            HeaderText.Text =
                $"{sourceTable} #{sourcePk}  →  {targetTable} #{(targetRecordId > 0 ? targetRecordId.ToString() : "(미상)")}";

            Loaded += async (_, _) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            StatusText.Text = "읽는 중…";
            try
            {
                var res = await RecordPreviewBuilder.BuildAsync(
                    _opt, _sourceTable, _sourcePk, _targetTable, _targetId);

                _src.Clear(); _tgt.Clear();
                foreach (var p in res.Source) _src.Add(p);
                foreach (var p in res.Target) _tgt.Add(p);

                SrcTitle.Text = res.SourceTitle;
                TgtTitle.Text = res.TargetTitle;
                StatusText.Text = res.Note;
            }
            catch (Exception ex)
            {
                StatusText.Text = "로드 실패: " + ex.Message;
            }
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();
    }
}
