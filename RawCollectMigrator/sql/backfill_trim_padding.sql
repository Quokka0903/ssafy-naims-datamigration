/* ════════════════════════════════════════════════════════════════════════════
   backfill_trim_padding.sql
     NAIMS2 에 이미 적재된 문자열 공백 패딩 오염을 정리한다.

   무엇을 고치나
     · 유형 A (값 + 후행 공백) : 'ASW' + 공백17  →  'ASW'
     · 유형 B (공백만 저장)    : 공백 20개        →  ''      (NULL 이 아니다)

   왜 NULL 이 아니라 '' 인가 — NAIMS2 의 STD_* 문자열 컬럼 130개 중 97개가 NOT NULL 이라
   NULL 로는 애초에 통일이 불가능하다. 두 표현이 섞이면 검색 조건이 IS NULL 과 = '' 를
   둘 다 봐야 하고, 한 쪽을 빠뜨리는 순간 조용히 틀린다. (2026-08-20 단말기팀 합의)

   무엇을 고치지 않나
     · 선행 공백 — LTRIM 은 걸지 않는다. COMMENT·*_RMKS 는 자유 입력 필드라
       사용자가 의도한 들여쓰기일 수 있고, 실측상 진짜 선행 공백 데이터는 0건이다.
     · '-' 플레이스홀더 — 레거시가 "없음"이라고 <b>판단해서 적은 값</b>이다.
       빈 값으로 바꾸면 "입력 안 함"과 구분이 사라지고 되돌릴 수 없다. 그대로 둔다.

   안전장치
     · 대상 테이블을 통째로 <테이블>_bak_YYMMDD 로 먼저 복사한다(@DoBackup=1).
     · 전체를 하나의 트랜잭션으로 묶는다. 도중에 실패하면 아무것도 반영되지 않는다.
     · @WhatIf=1 로 두면 계산·보고만 하고 UPDATE 를 실행하지 않는다.
     · _bak_ 테이블 자신은 대상에서 제외한다(원본 보존이 목적이므로).

   실행 방법 (Dev / Testbench 동일)
     sqlcmd -S "192.168.200.205,1433" -U NAIMS2DB -P *** -d NAIMS2 -i backfill_trim_padding.sql
   ════════════════════════════════════════════════════════════════════════════ */
SET NOCOUNT ON;
SET XACT_ABORT ON;

--------------------------------------------------------------------------------
-- 설정
--------------------------------------------------------------------------------
DECLARE @WhatIf   bit = 0;   -- 1 = 실제 UPDATE 없이 대상·건수만 본다
DECLARE @DoBackup bit = 1;   -- 1 = 대상 테이블을 _bak_YYMMDD 로 복사
DECLARE @Stamp    char(6) = CONVERT(char(6), GETDATE(), 12);   -- YYMMDD

PRINT '════════════════════════════════════════════════════════════════';
PRINT '  DB      : ' + DB_NAME() + '  @ ' + @@SERVERNAME;
PRINT '  시각    : ' + CONVERT(varchar(19), GETDATE(), 120);
PRINT '  모드    : ' + CASE WHEN @WhatIf = 1 THEN 'WHATIF (미반영)' ELSE 'APPLY (실제 반영)' END
                     + ' / 백업 ' + CASE WHEN @DoBackup = 1 THEN 'ON  (_bak_' + @Stamp + ')' ELSE 'OFF' END;
PRINT '════════════════════════════════════════════════════════════════';

--------------------------------------------------------------------------------
-- 1) 오염된 (테이블, 컬럼) 목록을 실측으로 만든다
--    ⚠ LEN(c) < DATALENGTH(c) 는 쓰지 않는다 — varchar 한글 1글자=2바이트라 대량 오탐.
--------------------------------------------------------------------------------
IF OBJECT_ID('tempdb..#pad') IS NOT NULL DROP TABLE #pad;
CREATE TABLE #pad (
    T          sysname,
    C          sysname,
    PAD_ROWS   int,
    BLANK_ROWS int,
    UPDATED    int NULL
);

DECLARE @t sysname, @c sysname, @q nvarchar(max);

DECLARE cur CURSOR FAST_FORWARD FOR
    SELECT t.name, c.name
    FROM sys.columns c
    JOIN sys.tables  t ON t.object_id = c.object_id
    JOIN sys.types   y ON y.user_type_id = c.user_type_id
    WHERE y.name IN ('varchar','char','nvarchar','nchar')
      AND c.is_computed = 0            -- 계산 컬럼은 UPDATE 대상이 아니다
      AND t.is_ms_shipped = 0
      AND t.name NOT LIKE '%[_]bak[_]%'
    ORDER BY t.name, c.column_id;

OPEN cur; FETCH NEXT FROM cur INTO @t, @c;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @q = N'INSERT #pad (T,C,PAD_ROWS,BLANK_ROWS)
        SELECT @t, @c,
          SUM(CASE WHEN DATALENGTH(' + QUOTENAME(@c) + N') > DATALENGTH(RTRIM(' + QUOTENAME(@c) + N')) THEN 1 ELSE 0 END),
          SUM(CASE WHEN DATALENGTH(' + QUOTENAME(@c) + N') > 0 AND LEN(' + QUOTENAME(@c) + N') = 0 THEN 1 ELSE 0 END)
        FROM ' + QUOTENAME(@t) + N' WITH (NOLOCK)
        HAVING SUM(CASE WHEN DATALENGTH(' + QUOTENAME(@c) + N') > DATALENGTH(RTRIM(' + QUOTENAME(@c) + N')) THEN 1 ELSE 0 END) > 0';
    EXEC sp_executesql @q, N'@t sysname, @c sysname', @t = @t, @c = @c;
    FETCH NEXT FROM cur INTO @t, @c;
END
CLOSE cur; DEALLOCATE cur;

PRINT '── 대상 (오염 컬럼 목록) ─────────────────────────────────────────';
SELECT T, C, PAD_ROWS, BLANK_ROWS FROM #pad ORDER BY T, C;
SELECT COUNT(*) AS TARGET_COLUMNS, ISNULL(SUM(PAD_ROWS),0) AS PAD_CELLS,
       ISNULL(SUM(BLANK_ROWS),0) AS BLANK_ONLY_CELLS,
       COUNT(DISTINCT T) AS TARGET_TABLES
FROM #pad;

IF NOT EXISTS (SELECT 1 FROM #pad)
BEGIN
    PRINT '오염 없음 — 할 일이 없습니다.';
    RETURN;
END

--------------------------------------------------------------------------------
-- 2) 백업 — 대상 테이블을 통째로 복사한다.
--    같은 이름이 이미 있으면 만들지 않는다(같은 날 두 번 돌려도 최초 원본이 보존된다).
--------------------------------------------------------------------------------
IF @DoBackup = 1
BEGIN
    PRINT '── 백업 ──────────────────────────────────────────────────────────';
    DECLARE @bak sysname;
    DECLARE bcur CURSOR FAST_FORWARD FOR SELECT DISTINCT T FROM #pad ORDER BY T;
    OPEN bcur; FETCH NEXT FROM bcur INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @bak = @t + N'_bak_' + @Stamp;
        IF OBJECT_ID(QUOTENAME(@bak)) IS NULL
        BEGIN
            SET @q = N'SELECT * INTO ' + QUOTENAME(@bak) + N' FROM ' + QUOTENAME(@t) + N';';
            EXEC sp_executesql @q;
            SET @q = N'SELECT ''' + @bak + N''' AS BACKUP_TABLE, COUNT(*) AS ROWS_ FROM ' + QUOTENAME(@bak);
            EXEC sp_executesql @q;
        END
        ELSE
            PRINT '  · ' + @bak + ' 이미 있음 — 유지(덮어쓰지 않음)';
        FETCH NEXT FROM bcur INTO @t;
    END
    CLOSE bcur; DEALLOCATE bcur;
END

--------------------------------------------------------------------------------
-- 3) 백필 — 하나의 트랜잭션. RTRIM 은 후행 공백만 제거한다(이관 코드의 LegacyText 와 같은 규칙).
--    유형 B 는 RTRIM 결과가 자연히 '' 가 되므로 별도 처리가 필요 없다.
--------------------------------------------------------------------------------
IF @WhatIf = 1
BEGIN
    PRINT '── WHATIF: UPDATE 를 실행하지 않고 종료합니다 ────────────────────';
    RETURN;
END

PRINT '── 백필 실행 ─────────────────────────────────────────────────────';
BEGIN TRANSACTION;

DECLARE ucur CURSOR FAST_FORWARD FOR SELECT T, C FROM #pad ORDER BY T, C;
OPEN ucur; FETCH NEXT FROM ucur INTO @t, @c;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @q = N'UPDATE ' + QUOTENAME(@t) + N'
                  SET ' + QUOTENAME(@c) + N' = RTRIM(' + QUOTENAME(@c) + N')
                WHERE DATALENGTH(' + QUOTENAME(@c) + N') > DATALENGTH(RTRIM(' + QUOTENAME(@c) + N'));
               UPDATE #pad SET UPDATED = @@ROWCOUNT WHERE T = @t AND C = @c;';
    EXEC sp_executesql @q, N'@t sysname, @c sysname', @t = @t, @c = @c;
    FETCH NEXT FROM ucur INTO @t, @c;
END
CLOSE ucur; DEALLOCATE ucur;

COMMIT TRANSACTION;

PRINT '── 갱신 건수 ─────────────────────────────────────────────────────';
SELECT T, C, PAD_ROWS AS EXPECTED, UPDATED FROM #pad ORDER BY T, C;
SELECT ISNULL(SUM(UPDATED),0) AS TOTAL_UPDATED, COUNT(*) AS COLUMNS_TOUCHED,
       COUNT(DISTINCT T) AS TABLES_TOUCHED FROM #pad;
SELECT T, C, PAD_ROWS AS EXPECTED, UPDATED FROM #pad
WHERE ISNULL(UPDATED,-1) <> PAD_ROWS;     -- 기대와 다르면 여기 뜬다(0행이어야 정상)

PRINT '완료. 검증은 sql/verify_padding.sql 을 실행할 것 (4-1·4-2 모두 0행이어야 합격).';
