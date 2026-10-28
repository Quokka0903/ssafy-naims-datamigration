/* ════════════════════════════════════════════════════════════════════════════
   verify_padding.sql — NAIMS2 문자열 공백 패딩 오염 검증 (읽기 전용)

   합격 기준: 두 결과 집합이 모두 0행.
     · 유형 A (값 + 후행 공백) : DATALENGTH(c) > DATALENGTH(RTRIM(c))
     · 유형 B (공백만 저장)    : DATALENGTH(c) > 0 AND LEN(c) = 0

   ⚠ LEN(c) < DATALENGTH(c) 로 판정하지 말 것.
     varchar 에 저장된 한글은 1글자가 2바이트라 정상 데이터가 대량 오탐된다.
     반드시 DATALENGTH(RTRIM(c)) 와 대조할 것.
   ⚠ col = '' 비교로 유형 B 를 찾을 수 없다.
     SQL Server 의 = 비교는 후행 공백을 무시해 "공백 20개" 도 '' 와 같다고 판정한다.
     LEN(c)=0 AND DATALENGTH(c)>0 형태를 쓸 것.

   백필로 만든 _bak_ 테이블은 원본 보존이 목적이므로 검사 대상에서 뺀다.
   ════════════════════════════════════════════════════════════════════════════ */
SET NOCOUNT ON;

IF OBJECT_ID('tempdb..#chk') IS NOT NULL DROP TABLE #chk;
CREATE TABLE #chk (T sysname, C sysname, PAD_ROWS int, BLANK_ONLY_ROWS int, ERR nvarchar(400) NULL);

DECLARE @t sysname, @c sysname, @q nvarchar(max);
DECLARE cur CURSOR FAST_FORWARD FOR
    SELECT t.name, c.name
    FROM sys.columns c
    JOIN sys.tables  t ON t.object_id = c.object_id
    JOIN sys.types   y ON y.user_type_id = c.user_type_id
    WHERE y.name IN ('varchar','char','nvarchar','nchar')
      AND c.is_computed = 0
      AND t.name NOT LIKE '%[_]bak[_]%'
      AND t.is_ms_shipped = 0
    ORDER BY t.name, c.column_id;

OPEN cur; FETCH NEXT FROM cur INTO @t, @c;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @q = N'INSERT #chk (T,C,PAD_ROWS,BLANK_ONLY_ROWS)
        SELECT @t, @c,
          SUM(CASE WHEN DATALENGTH(' + QUOTENAME(@c) + N') > DATALENGTH(RTRIM(' + QUOTENAME(@c) + N')) THEN 1 ELSE 0 END),
          SUM(CASE WHEN DATALENGTH(' + QUOTENAME(@c) + N') > 0 AND LEN(' + QUOTENAME(@c) + N') = 0 THEN 1 ELSE 0 END)
        FROM ' + QUOTENAME(@t) + N' WITH (NOLOCK)';
    BEGIN TRY
        EXEC sp_executesql @q, N'@t sysname, @c sysname', @t = @t, @c = @c;
    END TRY
    BEGIN CATCH
        INSERT #chk (T,C,PAD_ROWS,BLANK_ONLY_ROWS,ERR) VALUES (@t,@c,NULL,NULL,ERROR_MESSAGE());
    END CATCH
    FETCH NEXT FROM cur INTO @t, @c;
END
CLOSE cur; DEALLOCATE cur;

PRINT '── 4-1  유형 A(후행 공백) 검출 — 0행이어야 합격 ─────────────────────';
SELECT T, C, PAD_ROWS FROM #chk WHERE ISNULL(PAD_ROWS,0) > 0 ORDER BY T, C;

PRINT '── 4-2  유형 B(공백만 저장) 검출 — 0행이어야 합격 ───────────────────';
SELECT T, C, BLANK_ONLY_ROWS FROM #chk WHERE ISNULL(BLANK_ONLY_ROWS,0) > 0 ORDER BY T, C;

PRINT '── 요약 ──────────────────────────────────────────────────────────────';
SELECT DB_NAME()                                    AS [DB],
       COUNT(*)                                     AS SCANNED_COLUMNS,
       SUM(CASE WHEN ISNULL(PAD_ROWS,0)>0 THEN 1 ELSE 0 END)        AS BAD_COLUMNS_A,
       ISNULL(SUM(PAD_ROWS),0)                                      AS BAD_CELLS_A,
       SUM(CASE WHEN ISNULL(BLANK_ONLY_ROWS,0)>0 THEN 1 ELSE 0 END) AS BAD_COLUMNS_B,
       ISNULL(SUM(BLANK_ONLY_ROWS),0)                               AS BAD_CELLS_B,
       SUM(CASE WHEN ERR IS NOT NULL THEN 1 ELSE 0 END)             AS SCAN_ERRORS
FROM #chk;

SELECT T, C, ERR FROM #chk WHERE ERR IS NOT NULL;
