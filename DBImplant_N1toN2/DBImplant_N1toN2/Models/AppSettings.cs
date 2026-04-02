using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DBImplant_N1toN2.Models
{
    internal class AppSettings
    {
        /// <summary>
        /// 런타임에 변경 가능한 루트 경로 설정.
        /// 기본값은 로컬 D:\MSU 기준이며, UI에서 폴더 선택 다이얼로그로 변경 가능.
        /// </summary>
        public static class Roots
        {
            public static string CollectRaw         { get; set; } = @"D:\MSU\수집원음자료";
            public static string NarrowbandRaw      { get; set; } = @"D:\MSU\협대역원음자료";
            public static string ShortPeriodRaw     { get; set; } = @"D:\MSU\단주기원음자료";
            public static string ClassificationSnippet { get; set; } = @"D:\MSU\CLASSIFICATIONDB\SNIPPET";

            /// <summary>
            /// MSU 루트 경로 기반으로 모든 하위 경로를 일괄 설정한다.
            /// 예: basePath = "D:\MSU" → 수집원음자료, 협대역원음자료, 단주기원음자료, CLASSIFICATIONDB\SNIPPET
            /// </summary>
            public static void SetMsuRoot(string basePath)
            {
                CollectRaw            = System.IO.Path.Combine(basePath, "수집원음자료");
                NarrowbandRaw         = System.IO.Path.Combine(basePath, "협대역원음자료");
                ShortPeriodRaw        = System.IO.Path.Combine(basePath, "단주기원음자료");
                ClassificationSnippet = System.IO.Path.Combine(basePath, "CLASSIFICATIONDB", "SNIPPET");
            }
        }
    }
}
