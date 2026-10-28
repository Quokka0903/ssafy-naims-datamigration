using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RawCollectMigrator.Features.DbToDb
{
    // [찾기:불일치셀]
    /// <summary>
    /// 이 셀이 <b>어긋난 값</b>인지 판단해 색을 돌려준다.
    ///
    /// 판단은 여기서 다시 하지 않는다 — 대조할 때 이미 정해 둔 결과를
    /// <see cref="RecordMatchBuilder.ColDiffCells"/> 숨은 컬럼에서 읽기만 한다.
    /// 화면이 자기 규칙으로 한 번 더 비교하면(공백패딩·부동소수 오차 등) 언젠가
    /// "빨간데 '차이' 열은 비어 있음" 같은 모순이 생기고, 그 순간 사람은 색을 믿지 않게 된다.
    ///
    /// 색 선택 — 행은 옅은 빨강(어느 레코드가 문제인지), 셀은 진한 빨강(어느 값이 문제인지).
    /// 글자색은 빨강의 보색인 청록을 쓴다. 진한 빨강 위의 흰 글씨는 주변 흰 글씨와 구별되지 않아
    /// "여기만 다르다"는 신호가 죽는다. 보색이면 눈이 먼저 간다.
    /// </summary>
    public sealed class MismatchBrushConverter : IMultiValueConverter
    {
        /// <summary>어긋난 셀 배경 — 진한 빨강.</summary>
        public static readonly SolidColorBrush CellBackground = Frozen(0x7E, 0x1C, 0x1C);

        /// <summary>
        /// 어긋난 셀 글자 — 빨강의 보색(청록)을 <b>밝게</b> 쓴다.
        ///
        /// 보색만으로는 부족하다. 색상환에서 반대라도 밝기가 비슷하면 글자가 배경에 잠긴다 —
        /// 실제로 처음 고른 청록은 진한 빨강 위에서 어둡게 깔려 값을 읽을 수 없었다.
        /// 대비는 <b>색상 차</b>가 아니라 <b>밝기 차</b>가 만든다. 그래서 보색 방향은 지키되
        /// 밝기를 끝까지 올린다(#D6FFFF, 명도 ≈0.95 : 배경 ≈0.10).
        /// </summary>
        public static readonly SolidColorBrush CellForeground = Frozen(0xD6, 0xFF, 0xFF);

        // [NAIMS-ISSUE:UNSET-FALLBACK] 평상시 값은 DependencyProperty.UnsetValue 로 두면 안 된다.
        //   Setter 의 바인딩이 UnsetValue 를 내면 <BasedOn 스타일의 설정으로 되돌아가지 않고>
        //   그 속성의 <기본값>으로 떨어진다. Control.Foreground 의 기본값은 시스템 검정이라,
        //   어두운 바탕 위에서 글자가 거의 안 보이게 된다 — 실측: 레코드 매칭 표만 글자가 어두웠다.
        //   그래서 어긋나지 않은 칸에는 <테마의 평상시 글자색>을 명시적으로 돌려준다.
        //   같은 것을 보는 두 화면(테이블별 대조 / 레코드 매칭)의 글자색이 달라서는 안 된다.
        private static Brush? _normalFg;
        private static Brush NormalForeground =>
            _normalFg ??= Application.Current?.TryFindResource("BrushTextPrimary") as Brush
                          ?? Frozen(0xd2, 0xd2, 0xe0);

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        /// <param name="values">[0] 열 머리(=DataTable 컬럼명), [1] 행(DataRowView).</param>
        /// <param name="parameter">"fg" 면 글자색, 그 외에는 배경색.</param>
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (!IsMismatch(values)) return Normal(parameter);

            return (parameter as string) switch
            {
                "fg" => CellForeground,
                // 색을 못 보는 사람도, 색이 뭉개진 화면에서도 알아볼 수 있게 굵기로 한 번 더 말한다.
                "bold" => FontWeights.Bold,
                _ => CellBackground,
            };
        }

        /// <summary>어긋나지 않은 칸의 평상시 값 — '없음' 이 아니라 실제 값을 준다.</summary>
        private static object Normal(object? parameter) => (parameter as string) switch
        {
            "fg" => NormalForeground,
            "bold" => FontWeights.Normal,
            _ => Brushes.Transparent,      // 배경은 비워 둬야 행 색(불일치 옅은 빨강)이 비쳐 보인다
        };

        private static bool IsMismatch(object[] values)
        {
            if (values is not { Length: >= 2 }) return false;
            if (values[0] is not string header || header.Length == 0) return false;
            if (values[1] is not DataRowView v) return false;

            var row = v.Row;
            if (row.RowState == DataRowState.Detached) return false;
            if (!row.Table.Columns.Contains(RecordMatchBuilder.ColDiffCells)) return false;
            if (row[RecordMatchBuilder.ColDiffCells] is not string marks || marks.Length == 0) return false;
            return marks.Contains("|" + header + "|", StringComparison.Ordinal);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
