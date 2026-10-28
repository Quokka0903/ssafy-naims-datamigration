-- ════════════════════════════════════════════════════════════════════════
--  01_build_staging.sql
--  목적: 로컬 리그(LEGACYNAIMS)를 "classic exp 안전" 스테이징 스키마(LEGACYEXP)로 복제.
--        BINARY_DOUBLE / BINARY_FLOAT 는 classic exp 가 못 뜬다(EXP-00104) → NUMBER 로 CAST.
--        deferred_segment_creation=FALSE 로 빈 테이블도 세그먼트를 강제 생성(EXP-00011 방지).
--  결과: LEGACYEXP 스키마에 원본과 "같은 이름"의 테이블 42개(숫자형만 NUMBER 로 치환).
--        → 이걸 테이블별 classic exp 하면 backup.bat 형태의 OracleTable_*.DMP 가 나온다.
--  접속: system(DBA). 재실행 안전(기존 LEGACYEXP 를 DROP 후 재생성).
--  정리: 끝나면 필요없을 때  DROP USER LEGACYEXP CASCADE;  로 제거하면 된다.
-- ════════════════════════════════════════════════════════════════════════
set serveroutput on size unlimited
whenever sqlerror continue

-- 1) 스테이징 스키마 재생성 --------------------------------------------------
DECLARE
  v_exists number;
BEGIN
  SELECT count(*) INTO v_exists FROM all_users WHERE username='LEGACYEXP';
  IF v_exists > 0 THEN
    EXECUTE IMMEDIATE 'DROP USER LEGACYEXP CASCADE';
    dbms_output.put_line('-- 기존 LEGACYEXP DROP');
  END IF;
END;
/
CREATE USER LEGACYEXP IDENTIFIED BY expwork
  DEFAULT TABLESPACE USERS TEMPORARY TABLESPACE TEMP QUOTA UNLIMITED ON USERS;
GRANT CREATE SESSION TO LEGACYEXP;

-- 2) 원본 테이블을 NUMBER-안전 복제(같은 이름) -------------------------------
DECLARE
  v_sql  CLOB;
  v_ok   number := 0;
  v_err  number := 0;
BEGIN
  -- 빈 테이블도 세그먼트를 즉시 만들어 classic exp 가 인식하게 한다.
  EXECUTE IMMEDIATE 'ALTER SESSION SET deferred_segment_creation=FALSE';

  FOR t IN (SELECT table_name FROM all_tables WHERE owner='LEGACYNAIMS' ORDER BY table_name) LOOP
    v_sql := 'CREATE TABLE LEGACYEXP."' || t.table_name || '" AS SELECT ';
    FOR c IN (SELECT column_name, data_type
                FROM all_tab_columns
               WHERE owner='LEGACYNAIMS' AND table_name=t.table_name
               ORDER BY column_id) LOOP
      IF c.data_type IN ('BINARY_DOUBLE','BINARY_FLOAT') THEN
        v_sql := v_sql || 'CAST("' || c.column_name || '" AS NUMBER) "' || c.column_name || '",';
      ELSE
        v_sql := v_sql || '"' || c.column_name || '",';
      END IF;
    END LOOP;
    v_sql := rtrim(v_sql, ',') || ' FROM LEGACYNAIMS."' || t.table_name || '"';
    BEGIN
      EXECUTE IMMEDIATE v_sql;
      v_ok := v_ok + 1;
      dbms_output.put_line('OK  ' || t.table_name);
    EXCEPTION WHEN OTHERS THEN
      v_err := v_err + 1;
      dbms_output.put_line('ERR ' || t.table_name || ' : ' || sqlerrm);
    END;
  END LOOP;

  dbms_output.put_line('=== staged OK=' || v_ok || ' ERR=' || v_err || ' ===');
END;
/

-- 3) 확인용: 스테이징에 남은 BINARY 타입(0 이어야 정상) ----------------------
prompt == 스테이징에 남은 BINARY 컬럼(0 이어야 함) ==
select count(*) as remaining_binary
  from all_tab_columns
 where owner='LEGACYEXP' and data_type in ('BINARY_DOUBLE','BINARY_FLOAT');

prompt == 스테이징 테이블 수 ==
select count(*) as staged_tables from all_tables where owner='LEGACYEXP';
