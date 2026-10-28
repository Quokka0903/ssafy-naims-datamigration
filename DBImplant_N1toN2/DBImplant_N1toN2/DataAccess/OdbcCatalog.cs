using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace DBImplant_N1toN2.DataAccess
{
    /// <summary>등록된 DSN 한 건.</summary>
    public sealed record OdbcDsn(string Name, string Driver, string Scope);

    /// <summary>
    /// 이 PC에 설치된 ODBC 드라이버·등록된 DSN 을 Windows 레지스트리에서 읽는다.
    /// 접속문자열의 Driver={...} 이름과 DSN 별칭을 사용자가 직접 알 필요 없이 클릭으로 채우게 한다.
    ///
    /// ⚠ 비트수(32/64) 주의: 64비트 프로세스는 64비트 ODBC 항목만, 32비트는 32비트 항목만 본다.
    /// </summary>
    public static class OdbcCatalog
    {
        private static readonly RegistryView View =
            Environment.Is64BitProcess ? RegistryView.Registry64 : RegistryView.Registry32;

        public static string Bitness => Environment.Is64BitProcess ? "64비트" : "32비트";

        public static IReadOnlyList<string> Drivers()
        {
            var list = new List<string>();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, View);
                using var k = baseKey.OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers");
                if (k != null)
                    foreach (var name in k.GetValueNames())
                        if (!string.IsNullOrWhiteSpace(name)) list.Add(name);
            }
            catch { }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        public static IReadOnlyList<OdbcDsn> DataSources()
        {
            var list = new List<OdbcDsn>();
            Collect(RegistryHive.LocalMachine, "시스템", list);
            Collect(RegistryHive.CurrentUser, "사용자", list);
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private static void Collect(RegistryHive hive, string scope, List<OdbcDsn> into)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, View);
                using var k = baseKey.OpenSubKey(@"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources");
                if (k == null) return;
                foreach (var name in k.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var driver = Convert.ToString(k.GetValue(name)) ?? "";
                    into.Add(new OdbcDsn(name, driver, scope));
                }
            }
            catch { }
        }

        /// <summary>
        /// 기존 접속문자열에서 Driver={...} / DSN=... 토큰만 골라내고 새 토큰을 앞에 붙인다.
        /// 서버·계정 등 나머지 값은 보존한다.
        /// </summary>
        public static string InjectTarget(string connStr, string newToken)
        {
            var kept = new List<string>();
            foreach (var part in (connStr ?? "").Split(';'))
            {
                var t = part.Trim();
                if (t.Length == 0) continue;
                var key = t.Split('=', 2)[0].Trim();
                if (key.Equals("Driver", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.Equals("DSN", StringComparison.OrdinalIgnoreCase)) continue;
                kept.Add(t);
            }
            var rest = string.Join(";", kept);
            return newToken.TrimEnd(';') + ";" + (rest.Length > 0 ? rest + ";" : "");
        }
    }
}
