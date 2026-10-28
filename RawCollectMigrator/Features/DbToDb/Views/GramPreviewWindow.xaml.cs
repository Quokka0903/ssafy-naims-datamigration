using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace RawCollectMigrator.Features.DbToDb
{
    /// <summary>
    /// 레거시 .grm(그램/스펙트로그램) 파일을 열어 헤더 필드·진폭 통계·R8 66B 변환 결과·헥스 덤프를
    /// 화면에 미리보기하는 진단용 창. 실제 마이그레이션 전에 GramConverter/LegacyGrmReader/GramHeader66
    /// 파싱과 R8 변환이 올바른지 육안으로 확인하는 용도이며, 원본 파일을 수정하지 않는 읽기 전용 도구다.
    /// </summary>
    public partial class GramPreviewWindow : Window
    {
        /// <summary>
        /// XAML 리소스를 초기화하고 빈 미리보기 창을 생성한다.
        /// </summary>
        public GramPreviewWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// [찾아보기] 버튼 핸들러. .grm 파일 선택 대화상자를 열어 선택된 경로를 PathBox에 채운다(취소 시 무변경).
        /// </summary>
        /// <param name="sender">이벤트 발생 컨트롤(찾아보기 버튼).</param>
        /// <param name="e">라우티드 이벤트 인자.</param>
        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = ".grm 파일 선택",
                Filter = "gram files (*.grm)|*.grm|All files (*.*)|*.*",
                Multiselect = false
            };
            if (dlg.ShowDialog(this) == true) PathBox.Text = dlg.FileName;
        }

        /// <summary>
        /// [분석] 버튼 핸들러. PathBox 경로의 .grm 파일을 GramConverter.Inspect로 분석해 결과 패널을 다시 그린다.
        /// 파일 정보·포맷 판별(R8 66B 헤더 vs 레거시)·R8 공통헤더 필드·레거시 헤더 상세·진폭 통계를 표시하고,
        /// 레거시 포맷이면 R8 66B 변환을 시험 실행해 왕복 검증 결과와 앞 96B 헥스 덤프까지 덧붙인다.
        /// 부작용: 파일 IO(원본 읽기 전용), ResultPanel의 UI 요소를 전부 지우고 재구성한다. 오류는 붉은 안내 줄로 표시하고 반환한다.
        /// </summary>
        /// <param name="sender">이벤트 발생 컨트롤(분석 버튼).</param>
        /// <param name="e">라우티드 이벤트 인자.</param>
        private void OnInspect(object sender, RoutedEventArgs e)
        {
            ResultPanel.Children.Clear();

            var path = PathBox.Text.Trim();
            if (string.IsNullOrEmpty(path))
            {
                ResultPanel.Children.Add(MakeLine("❌ 파일 경로가 비어 있습니다.", "#ff6666"));
                return;
            }
            if (!File.Exists(path))
            {
                ResultPanel.Children.Add(MakeLine("❌ 파일을 찾을 수 없습니다:\n  " + path, "#ff6666"));
                return;
            }

            GramConverter.GramInspection insp;
            try { insp = GramConverter.Inspect(path); }
            catch (Exception ex)
            {
                ResultPanel.Children.Add(MakeLine("❌ 분석 중 오류: " + ex.Message, "#ff6666"));
                return;
            }

            var color = insp.IsR8Header ? "#88cc88" : "#ffaa44";
            ResultPanel.Children.Add(MakeSection("📄 파일 정보"));
            ResultPanel.Children.Add(MakeKv("경로",    insp.FilePath));
            ResultPanel.Children.Add(MakeKv("크기",    $"{insp.FileSize:N0} bytes ({insp.FileSize / 1024.0 / 1024:F2} MB)"));

            ResultPanel.Children.Add(MakeSection("🏷️ 포맷 판별"));
            ResultPanel.Children.Add(MakeLine(
                insp.IsR8Header ? "✓ 신규 R8 66B 헤더" : "◇ 레거시 포맷 (R8 변환 대상)", color));
            ResultPanel.Children.Add(MakeKv("진단", insp.Diagnostic));

            var h = insp.Header;
            if (h != null)
            {
                ResultPanel.Children.Add(MakeSection("📐 R8 공통헤더 필드"));
                ResultPanel.Children.Add(MakeKv("GramType",       h.GramType));
                ResultPanel.Children.Add(MakeKv("StartTime",      h.StartTime == DateTime.MinValue ? "(비정상)" : h.StartTime.ToString("yyyy-MM-dd HH:mm:ss.fff")));
                ResultPanel.Children.Add(MakeKv("EndTime",        h.EndTime   == DateTime.MinValue ? "(비정상)" : h.EndTime.ToString("yyyy-MM-dd HH:mm:ss.fff")));
                ResultPanel.Children.Add(MakeKv("FrameCount",     h.FrameCount.ToString("N0")));
                ResultPanel.Children.Add(MakeKv("IntegralTime",   $"{h.IntegralTime:F4} s"));
                ResultPanel.Children.Add(MakeKv("BinResolution",  $"{h.BinResolution:F4} Hz/bin"));
                ResultPanel.Children.Add(MakeKv("StartFreq",      $"{h.StartFreq:F2} Hz"));
                ResultPanel.Children.Add(MakeKv("EndFreq",        $"{h.EndFreq:F2} Hz"));
                ResultPanel.Children.Add(MakeKv("IntervalSec",    $"{h.IntervalSec:F6} s"));
                ResultPanel.Children.Add(MakeKv("추정 BinCount",  insp.EstimatedBinCount.ToString("N0")));
            }

            // ── 레거시 헤더 상세 ──
            var lg = insp.Legacy;
            if (lg is { Ok: true } && lg.Kind != LegacyGrmKind.R8)
            {
                ResultPanel.Children.Add(MakeSection("📐 레거시 헤더 필드"));
                ResultPanel.Children.Add(MakeKv("포맷",       lg.KindText));
                ResultPanel.Children.Add(MakeKv("헤더 크기",  $"{lg.HeaderSize} B"));
                if (!string.IsNullOrEmpty(lg.GramTag))
                    ResultPanel.Children.Add(MakeKv("그램 태그", $"'{lg.GramTag}'  → R8 '{GramConverter.ToR8Tag(lg)}'"));
                if (lg.DetDate is { } d)
                    ResultPanel.Children.Add(MakeKv("탐지일자", d.ToString("yyyy-MM-dd")));
                ResultPanel.Children.Add(MakeKv("StartTime",   $"{lg.StartTimeMs:N0} ms"));
                ResultPanel.Children.Add(MakeKv("EndTime",     $"{lg.EndTimeMs:N0} ms"));
                ResultPanel.Children.Add(MakeKv("주파수 범위", $"{lg.StartFreq:F2} ~ {lg.EndFreq:F2} Hz"));
                ResultPanel.Children.Add(MakeKv("Resolution",  $"{lg.Resolution:F4} Hz/bin"));
                ResultPanel.Children.Add(MakeKv("FrameCount",  lg.FrameCount.ToString("N0")));
                ResultPanel.Children.Add(MakeKv("BinCount",    lg.BinCount.ToString("N0")));
                if (lg.IntegralTime != 0) ResultPanel.Children.Add(MakeKv("IntegralTime", $"{lg.IntegralTime:F4} s"));
                if (lg.UpdateTime   != 0) ResultPanel.Children.Add(MakeKv("UpdateTime",   $"{lg.UpdateTime:F4} s"));
                if (!string.IsNullOrEmpty(lg.Comment))              ResultPanel.Children.Add(MakeKv("Comment", lg.Comment));
                if (!string.IsNullOrEmpty(lg.AudioSnippetFileName)) ResultPanel.Children.Add(MakeKv("음원 파일", lg.AudioSnippetFileName));
                if (!string.IsNullOrEmpty(lg.DetSonar))             ResultPanel.Children.Add(MakeKv("탐지 소나", lg.DetSonar));
                if (lg.EventId != 0)                                ResultPanel.Children.Add(MakeKv("EventID", lg.EventId.ToString()));
                ResultPanel.Children.Add(MakeKv("ALI 블록", lg.Ali == null ? "없음" : $"{lg.Ali.Length:N0} B"));

                // 진폭 통계 — 역산이 제대로 됐는지 눈으로 확인.
                // 그램 종류마다 값 영역이 다르므로 무엇을 보고 있는지 함께 적는다.
                if (lg.Amplitude.Length > 0)
                {
                    float min = float.MaxValue, max = float.MinValue;
                    double sum = 0;
                    foreach (var v in lg.Amplitude)
                    {
                        if (v < min) min = v;
                        if (v > max) max = v;
                        sum += v;
                    }

                    var (label, note) = lg.Kind switch
                    {
                        LegacyGrmKind.NbSnippet =>
                            ("진폭(선형)", $"10^((BYTE−{lg.UsedBScale:0.##})/{lg.UsedAScale:0.##})"),
                        LegacyGrmKind.AiSnippet =>
                            ("진폭(상대 dB)", "(32·(BYTE÷4)+15.5)/255×18−12  · 원본이 8단계뿐"),
                        LegacyGrmKind.RawFloat =>
                            ("진폭(원본 float)", "값 영역 미확정 — 변환 없음"),
                        _ => ("진폭", "변환 없음"),
                    };

                    ResultPanel.Children.Add(MakeKv(label,
                        $"min {min:G6}  ·  avg {sum / lg.Amplitude.Length:G6}  ·  max {max:G6}   ({note})"));

                    if (!string.IsNullOrEmpty(lg.ScaleSource))
                        ResultPanel.Children.Add(MakeKv("스케일 출처", lg.ScaleSource));
                }

                // ── R8 변환 시험 ──
                ResultPanel.Children.Add(MakeSection("🔄 R8 66B 변환 시험"));
                var conv = GramConverter.ToR8(lg);
                if (conv.Ok)
                {
                    ResultPanel.Children.Add(MakeLine("✓ 변환 성공", "#88cc88"));
                    ResultPanel.Children.Add(MakeKv("결과", conv.Message));
                    ResultPanel.Children.Add(MakeKv("크기 변화",
                        $"{insp.FileSize:N0} B → {conv.Data.Length:N0} B  (BYTE→float 4배 + 프레임 TIMESTAMP)"));

                    // 변환 결과를 다시 읽어 왕복 검증
                    var back = GramHeader66.TryRead(conv.Data);
                    if (back != null)
                    {
                        ResultPanel.Children.Add(MakeKv("왕복 검증",
                            $"태그 '{back.GramType}' · 프레임 {back.FrameCount:N0} · " +
                            $"{back.StartFreq:F1}~{back.EndFreq:F1} Hz · {back.BinResolution:F4} Hz/bin"));
                        ResultPanel.Children.Add(MakeKv("시각",
                            $"{back.StartTime:yyyy-MM-dd HH:mm:ss.fff} ~ {back.EndTime:HH:mm:ss.fff}"));
                    }

                    ResultPanel.Children.Add(MakeSection("🔬 변환 결과 앞 96B"));
                    ResultPanel.Children.Add(MakeMono(HexDump(conv.Data, 96)));
                }
                else
                {
                    ResultPanel.Children.Add(MakeLine("✗ " + conv.Message, "#ffaa44"));
                }
            }

            ResultPanel.Children.Add(MakeSection("🔬 원본 앞 128B 헥스 덤프"));
            ResultPanel.Children.Add(MakeMono(BuildHexDump(path, 128)));
        }

        // ── 화면 요소 생성 헬퍼 ──

        /// <summary>결과 패널의 섹션 제목(파란 굵은 글씨) TextBlock을 생성한다.</summary>
        /// <param name="text">섹션 제목 문자열.</param>
        /// <returns>섹션 헤더용 <see cref="TextBlock"/>.</returns>
        private static TextBlock MakeSection(string text) => new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4a9eff")),
            Margin = new Thickness(4, 10, 4, 4),
            FontSize = 13
        };

        /// <summary>결과 패널에 한 줄 메시지(안내·성공·오류 등)를 표시하는 줄바꿈 가능한 TextBlock을 생성한다.</summary>
        /// <param name="text">표시할 메시지.</param>
        /// <param name="colorHex">글자색 헥스 코드(기본 밝은 회색). 오류는 붉은색, 성공은 녹색 등으로 지정.</param>
        /// <returns>메시지 한 줄용 <see cref="TextBlock"/>.</returns>
        private static TextBlock MakeLine(string text, string colorHex = "#e0e0e0") => new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)),
            Margin = new Thickness(4, 2, 4, 2),
            TextWrapping = TextWrapping.Wrap
        };

        /// <summary>
        /// "키: 값" 형태의 2열 그리드 행을 생성한다(왼쪽 140px 라벨, 오른쪽 Consolas 값). 헤더 필드 나열에 사용.
        /// </summary>
        /// <param name="key">라벨(콜론은 내부에서 자동 부착).</param>
        /// <param name="value">값 문자열.</param>
        /// <returns>키-값 한 행을 담은 <see cref="Grid"/>.</returns>
        private static Grid MakeKv(string key, string value)
        {
            var g = new Grid { Margin = new Thickness(12, 1, 4, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var k = new TextBlock { Text = key + ":", Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888")) };
            var v = new TextBlock { Text = value, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e0e0e0")), TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") };
            Grid.SetColumn(k, 0); Grid.SetColumn(v, 1);
            g.Children.Add(k); g.Children.Add(v);
            return g;
        }

        /// <summary>고정폭(Consolas) 텍스트 블록을 생성한다. 헥스 덤프 등 정렬이 중요한 다중 행 출력에 사용.</summary>
        /// <param name="text">고정폭으로 표시할 텍스트.</param>
        /// <returns>고정폭 출력용 <see cref="TextBlock"/>.</returns>
        private static TextBlock MakeMono(string text) => new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#c0c0c0")),
            Margin = new Thickness(12, 4, 4, 4)
        };

        /// <summary>
        /// 파일 앞부분 최대 maxBytes를 읽어 헥스 덤프 문자열로 변환한다. 파일 IO 부작용이 있으며,
        /// 읽기 실패 시 예외를 삼키고 실패 메시지 문자열을 반환한다.
        /// </summary>
        /// <param name="path">읽을 파일 경로.</param>
        /// <param name="maxBytes">읽어 덤프할 최대 바이트 수.</param>
        /// <returns>헥스 덤프 문자열(실패 시 "(헥스 덤프 실패: ...)").</returns>
        private static string BuildHexDump(string path, int maxBytes)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var buf = new byte[Math.Min(maxBytes, (int)Math.Min(fs.Length, maxBytes))];
                int read = fs.Read(buf, 0, buf.Length);
                return HexDump(buf, read);
            }
            catch (Exception ex) { return "(헥스 덤프 실패: " + ex.Message + ")"; }
        }

        /// <summary>
        /// 바이트 배열을 16바이트/행 형식의 헥스 덤프 문자열로 만든다(오프셋 · 16진 바이트 · ASCII 표현).
        /// 32~126 범위 밖 바이트는 '.'으로 치환한다.
        /// </summary>
        /// <param name="buf">덤프할 바이트 버퍼.</param>
        /// <param name="count">덤프할 바이트 수(버퍼 길이로 상한 제한됨).</param>
        /// <returns>여러 행으로 구성된 헥스 덤프 문자열.</returns>
        private static string HexDump(byte[] buf, int count)
        {
            int read = Math.Min(count, buf.Length);
            var sb = new StringBuilder();
            for (int i = 0; i < read; i += 16)
            {
                sb.AppendFormat("{0:X4}  ", i);
                for (int j = 0; j < 16; j++)
                {
                    if (i + j < read) sb.AppendFormat("{0:X2} ", buf[i + j]);
                    else sb.Append("   ");
                }
                sb.Append(" | ");
                for (int j = 0; j < 16 && i + j < read; j++)
                {
                    byte b = buf[i + j];
                    sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
