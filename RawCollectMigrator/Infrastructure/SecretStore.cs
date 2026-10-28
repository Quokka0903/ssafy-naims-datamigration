using System;
using System.Text;
using System.Text.RegularExpressions;

namespace RawCollectMigrator.Infrastructure
{
    // [가이드:비밀번호저장] [찾기:비밀번호저장] [NAIMS-ISSUE:NO-PLAINTEXT-SECRET]
    /// <summary>
    /// 비밀번호를 디스크에 남기는 <b>단 하나의 통로</b>.
    ///
    /// 규칙은 하나다 — <b>평문으로 남는 경로를 만들지 않는다.</b>
    /// 저장은 Windows DPAPI(현재 사용자 키)로만 하고, 암호화가 불가능한 환경이면
    /// 저장을 포기하고 비밀번호를 지운다. "암호화가 안 되니 평문으로라도" 는 없다.
    ///
    /// DPAPI 는 현재 <b>사용자 계정 + 이 PC</b> 에 묶인다. 설정 파일을 다른 계정·다른 PC 로
    /// 복사해도 풀리지 않는다(그때는 비밀번호만 다시 입력하면 된다).
    /// </summary>
    public static class SecretStore
    {
        /// <summary>보호된 문자열임을 알리는 접두어.</summary>
        public const string ProtectedPrefix = "dpapi:";

        /// <summary>현재 사용자 키로 암호화한다. 불가능하면 비밀번호를 지운 값을 돌려준다.</summary>
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var bytes = System.Security.Cryptography.ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plain), null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return ProtectedPrefix + Convert.ToBase64String(bytes);
            }
            catch
            {
                return StripPassword(plain);
            }
        }

        /// <summary>보호된 문자열을 푼다. 보호되지 않은 값은 그대로, 못 풀면 null.</summary>
        public static string? Unprotect(string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            if (!stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal)) return stored;
            try
            {
                var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(stored[ProtectedPrefix.Length..]), null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }

        /// <summary>접속문자열에서 비밀번호 <b>값</b>만 지운다(키는 남겨 사용자가 채우기 쉽게).</summary>
        public static string StripPassword(string conn) =>
            string.IsNullOrEmpty(conn) ? "" :
            Regex.Replace(conn, @"(?i)\b(password|pwd)\s*=\s*[^;]*", "$1=");

        /// <summary>접속문자열의 비밀번호 값(없으면 빈 문자열).</summary>
        public static string PasswordOf(string conn) =>
            Regex.Match(conn ?? "", @"(?i)\b(?:password|pwd)\s*=\s*([^;]*)").Groups[1].Value.Trim();

        /// <summary>비밀번호 인증을 쓰는데 값이 비어 있는지(= 지금 이대로는 붙을 수 없는지).</summary>
        public static bool NeedsPassword(string conn)
        {
            if (string.IsNullOrWhiteSpace(conn)) return false;
            if (Regex.IsMatch(conn, @"(?i)\b(?:trusted_connection|integrated\s+security)\s*=\s*(yes|true|sspi)"))
                return false;   // 통합 인증이면 비밀번호 자체가 없다
            return PasswordOf(conn).Length == 0;
        }

        // [찾기:비밀번호채우기]
        /// <summary>
        /// 접속문자열에 비밀번호를 끼워 넣는다. 키가 이미 있으면 값만 갈아 끼우고(표기 Pwd=/Password= 는 보존),
        /// 없으면 방언에 맞는 키를 덧붙인다(ODBC 계열은 Pwd, 그 외는 Password).
        /// 치환은 MatchEvaluator 로 한다 — 비밀번호에 $ 가 들어 있어도 그대로 들어가야 하기 때문이다.
        /// </summary>
        public static string WithPassword(string conn, string password)
        {
            if (string.IsNullOrEmpty(conn) || string.IsNullOrEmpty(password)) return conn ?? "";
            if (Regex.IsMatch(conn, @"(?i)\b(?:password|pwd)\s*="))
                return Regex.Replace(conn, @"(?i)\b(password|pwd)\s*=\s*[^;]*",
                                     m => m.Groups[1].Value + "=" + password);

            var key = Regex.IsMatch(conn, @"(?i)\b(?:uid|dsn|driver|dbq)\s*=") ? "Pwd" : "Password";
            var t = conn.TrimEnd();
            return t + (t.EndsWith(";", StringComparison.Ordinal) ? "" : ";") + key + "=" + password + ";";
        }

        /// <summary>로그·화면에 내보낼 때 쓰는 마스킹(비밀번호 값을 *** 로).</summary>
        public static string Mask(string conn) =>
            string.IsNullOrEmpty(conn) ? "" :
            Regex.Replace(conn, @"(?i)\b(password|pwd)\s*=\s*[^;]*", "$1=***");
    }
}
