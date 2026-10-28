namespace RawCollectMigrator.Infrastructure
{
    // ════════════════════════════════════════════════════════════════════════════
    //  [가이드:문자열정규화] [GUIDE:STRING-NORMALIZE] [찾기:공백패딩] [찾기:TrimEnd]
    //  ▣ 레거시 고정길이 문자열을 타겟에 싣기 전에 반드시 거쳐야 하는 <b>단일 창구</b>.
    //
    //  왜 필요한가 — NAIMS-I 은 Oracle/MsSQL 의 char(N) 고정폭 컬럼을 쓴다. 고정폭 컬럼은
    //  값이 짧으면 <b>DB 가 공백으로 채워서</b> 돌려준다. "ASW" 는 char(20) 에서 "ASW"+공백17 로 온다.
    //  그걸 그대로 varchar(20) 에 INSERT 하면 20바이트가 그대로 적재된다 — SQL 은 비교 시
    //  후행 공백을 무시하므로 <b>쿼리로는 멀쩡해 보이고</b>, 화면에서만 틀어진다:
    //    · WPF TextBlock 은 후행 공백을 폭에 포함한다 → HorizontalAlignment="Center" 가
    //      "공백 포함 문자열" 기준으로 정렬해 글자가 왼쪽으로 밀린다.
    //      실측: "ASW"=25.86px vs "ASW"+공백17=97.60px (셀 가용폭 100px).
    //    · LIKE / GROUP BY / 타 체계 Export 에도 그대로 번진다.
    //
    //  더 나쁜 형태가 하나 더 있다 — <b>"공백만" 문자열</b>. 레거시가 값 없음을 빈 char(20) 으로
    //  두면 공백 20개가 저장된다. NULL 도 '' 도 아니라서 IS NULL / = '' 필터가 전부 빗나간다.
    //  (SQL 의 = '' 비교는 후행 공백을 무시해 참이 되지만, DATALENGTH 는 20 이다.)
    //
    //  정책(2026-08-20 확정, 단말기팀 합의)
    //    · 후행 공백·NUL 제거(TrimEnd). <b>선행 공백은 건드리지 않는다</b> —
    //      COMMENT·*_RMKS 같은 자유 입력 필드에서 사용자가 의도한 들여쓰기일 수 있고,
    //      실측상 진짜 선행 공백 데이터는 0건이라 얻을 것이 없다.
    //    · 결과가 비면 <b>빈 문자열('')</b>. NULL 이 아니다 —
    //      NAIMS2 의 STD_* 문자열 컬럼 130개 중 97개가 NOT NULL 이라 NULL 로는 통일 자체가 불가능하다.
    //    · '-' 같은 레거시 플레이스홀더는 <b>값</b>이다. 건드리지 않는다
    //      ("입력 안 함"과 "없음이라고 판단함"은 다르고, 되돌릴 수 없다).
    //
    //  대응하는 백필 SQL 은 RTRIM 이다(SQL RTRIM 은 공백만 제거). 두 쪽 규칙을 일부러 맞춰 두었으니
    //  여기의 <see cref="TrimChars"/> 를 늘릴 때는 검증 쿼리(DATALENGTH(RTRIM(c)) 대조)도 같이 볼 것.
    // ════════════════════════════════════════════════════════════════════════════
    /// <summary>레거시 고정길이 문자열 정규화 — 이관되는 모든 문자열 값이 지나는 단일 창구.</summary>
    public static class LegacyText
    {
        /// <summary>
        /// 후행에서 잘라 낼 문자. 공백(0x20)은 char(N) 패딩이고, NUL 은 C 고정배열이
        /// DB 로 새어 들어온 경우에 대한 방어다(실측 0건이지만 비용이 없다).
        /// 여기에 문자를 더하면 백필 SQL 의 RTRIM 과 규칙이 어긋나므로 신중히.
        /// </summary>
        public static readonly char[] TrimChars = { ' ', '\0' };

        /// <summary>
        /// 문자열 하나를 정규화한다. null 은 빈 문자열이 된다(값을 요구하는 자리에서 쓸 때).
        /// null 을 null 로 보존해야 하는 자리에서는 <see cref="NormalizeValue"/> 를 쓸 것.
        /// </summary>
        /// <param name="s">원본 문자열.</param>
        /// <returns>후행 공백·NUL 을 제거한 문자열(원본이 null 이면 "").</returns>
        public static string Normalize(string? s) =>
            string.IsNullOrEmpty(s) ? "" : s.TrimEnd(TrimChars);

        /// <summary>
        /// DB 에서 읽은 임의의 값을 정규화한다. 문자열이면 후행 패딩을 떼고,
        /// 그 밖의 타입(숫자·날짜·byte[]·null·DBNull)은 <b>손대지 않고</b> 그대로 통과시킨다.
        /// 소스 행을 읽는 지점에서 이 함수를 거치게 하면, 이후 모든 경로
        /// (INSERT 값 결정·dedup 키 대조·자연키 재사용·미리보기)가 자동으로 정규화된 값을 본다.
        /// </summary>
        /// <param name="v">DB 리더에서 꺼낸 원시 값.</param>
        /// <returns>문자열이면 정규화된 문자열, 아니면 원본 그대로.</returns>
        public static object? NormalizeValue(object? v) =>
            v is string s ? s.TrimEnd(TrimChars) : v;

        /// <summary>
        /// 값이 "공백만 채워진 문자열"(유형 B)인지 판정한다. 진단·검증용.
        /// 빈 문자열("")은 이미 정규화된 상태이므로 false 다.
        /// </summary>
        /// <param name="v">검사할 값.</param>
        /// <returns>길이가 1 이상이면서 정규화하면 빈 문자열이 되는 경우 true.</returns>
        public static bool IsBlankPadded(object? v) =>
            v is string s && s.Length > 0 && s.TrimEnd(TrimChars).Length == 0;
    }
}
