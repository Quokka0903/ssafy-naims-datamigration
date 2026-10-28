-- =============================================================================
-- fix_prec_ss_argram_fk_260624.sql
-- 목적: PREC_SS_ARGRAM 의 FK 오결선 복구
--       REF_PREC_SS_EVENT_ID 가 (이름과 달리) PREC_SS_SEGMENT 를 참조하도록 잘못
--       생성된 FK 를, 올바르게 PREC_SS_EVENT(RECORD_ID) 를 참조하도록 바로잡는다.
--
-- 배경:
--   단주기(SS) 정밀 신호처리(서버 41013)는 PREC_SS_ARGRAM 에
--   REF_PREC_SS_EVENT_ID = (이벤트 RECORD_ID) 로 INSERT 한다. 그런데 컨벤션/마이그레이션이
--   이 컬럼의 FK 를 PREC_SS_SEGMENT.RECORD_ID 로 잘못 결선하여(형제 PREC_SS_SEGMENT/
--   PREC_SS_SPECTROGRAM 은 올바르게 PREC_SS_EVENT 참조), INSERT 마다 FK 547 충돌 →
--   전 이벤트 처리 실패 → 클라 "단주기 정밀 신호처리 결과 오류"(미전시) 가 발생한다.
--
-- 특징:
--   - 잘못된 FK 의 '이름'에 의존하지 않는다. 카탈로그(sys.foreign_keys)에서
--     "PREC_SS_ARGRAM.REF_PREC_SS_EVENT_ID -> PREC_SS_SEGMENT" 조건의 FK 를 찾아 제거한다.
--     (오프라인/구 백업 DB 의 FK 이름이 FK_PREC_SS_ARGRAM_PREC_SS_SEGMENT 든
--      구 FK_SS_PRECISE_AR_EVENT 든 무관하게 동작)
--   - 멱등: 이미 올바른 FK 가 있으면 아무 것도 하지 않는다.
--   - PREC_SS_ARGRAM 은 그동안 INSERT 가 전부 실패해 보통 0 행이므로 WITH CHECK 무위험.
--     혹시 기존 행이 있어 WITH CHECK 가 실패하면, 고아 행 정리 후 재실행한다.
-- =============================================================================
SET NOCOUNT ON;
GO

-- 1) 잘못된 FK 제거: PREC_SS_ARGRAM.REF_PREC_SS_EVENT_ID 가 PREC_SS_SEGMENT 를 참조하는 FK
DECLARE @wrongFk SYSNAME;
SELECT @wrongFk = fk.name
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
WHERE fk.parent_object_id   = OBJECT_ID(N'dbo.PREC_SS_ARGRAM')
  AND c.name                = N'REF_PREC_SS_EVENT_ID'
  AND fk.referenced_object_id = OBJECT_ID(N'dbo.PREC_SS_SEGMENT');

IF @wrongFk IS NOT NULL
BEGIN
    DECLARE @drop NVARCHAR(MAX) =
        N'ALTER TABLE dbo.PREC_SS_ARGRAM DROP CONSTRAINT ' + QUOTENAME(@wrongFk) + N';';
    EXEC sys.sp_executesql @drop;
    PRINT N'DROP 잘못된 FK: ' + @wrongFk + N' (REF_PREC_SS_EVENT_ID -> PREC_SS_SEGMENT)';
END
ELSE
    PRINT N'잘못된 FK(-> PREC_SS_SEGMENT) 없음 - DROP 생략';
GO

-- 2) 올바른 FK 생성: REF_PREC_SS_EVENT_ID -> PREC_SS_EVENT.RECORD_ID (형제 SPECTROGRAM 과 동일)
IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys fk
    JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
    JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
    WHERE fk.parent_object_id     = OBJECT_ID(N'dbo.PREC_SS_ARGRAM')
      AND c.name                  = N'REF_PREC_SS_EVENT_ID'
      AND fk.referenced_object_id = OBJECT_ID(N'dbo.PREC_SS_EVENT')
)
BEGIN
    ALTER TABLE dbo.PREC_SS_ARGRAM WITH CHECK
        ADD CONSTRAINT FK_PREC_SS_ARGRAM_PREC_SS_EVENT
        FOREIGN KEY (REF_PREC_SS_EVENT_ID) REFERENCES dbo.PREC_SS_EVENT (RECORD_ID);
    PRINT N'ADD 올바른 FK: FK_PREC_SS_ARGRAM_PREC_SS_EVENT (REF_PREC_SS_EVENT_ID -> PREC_SS_EVENT.RECORD_ID)';
END
ELSE
    PRINT N'올바른 FK(-> PREC_SS_EVENT) 이미 존재 - ADD 생략';
GO

-- 3) 검증: PREC_SS_ARGRAM 의 FK 가 이제 PREC_SS_EVENT 를 가리키는지 확인
SELECT fk.name AS fk_name,
       OBJECT_NAME(fk.referenced_object_id) AS ref_table
FROM sys.foreign_keys fk
WHERE fk.parent_object_id = OBJECT_ID(N'dbo.PREC_SS_ARGRAM');
GO
