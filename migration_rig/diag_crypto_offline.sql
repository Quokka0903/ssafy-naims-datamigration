-- ════════════════════════════════════════════════════════════════════════
--  diag_crypto_offline.sql
--  폐쇄망(실데이터) Oracle 에서 이름컬럼 암호화 실태를 "문자셋 무관"하게 진단한다.
--  결과를 전부 RAWTOHEX 로 뽑으므로, 터미널/클라이언트 NLS 와 상관없이 원본 바이트가 보인다.
--  → 이 출력을 그대로 복사해 넘겨주면, charset 문제인지 암호문 손상인지 판별 가능.
--
--  실행: sqlplus <이관계정>/<pwd>@<폐쇄망DB>  @diag_crypto_offline.sql
--        (이관이 붙는 그 계정으로 붙어야 실제 이관과 동일 조건)
--        NLS 설정 불필요 — 모든 값이 hex 로 나온다.
-- ════════════════════════════════════════════════════════════════════════
set linesize 400 pagesize 200 feedback off trimspool on
set serveroutput on size unlimited

prompt ==================== [1] 이름컬럼 저장 타입 ====================
col owner format a14
col table_name format a14
col column_name format a22
col data_type format a12
select owner, table_name, column_name, data_type, data_length, char_used
  from all_tab_columns
 where (owner, table_name) in (
        select owner, table_name from all_tables
         where table_name in ('CLASSES','VESSEL_DATA','ACTIVE_SONAR','TRANSIENT'))
   and column_name like '%NAME%'
 order by table_name, column_id;

prompt ==================== [2] CRYPTO/CRYPTIT 오브젝트 ====================
col object_name format a12
select owner, object_name, object_type, status
  from all_objects where upper(object_name) in ('CRYPTO','CRYPTIT')
 order by owner, object_name, object_type;

prompt ==================== [3] DBMS_OBFUSCATION_TOOLKIT 가용? ====================
select count(*) as obfus_toolkit_visible from all_objects
 where object_name='DBMS_OBFUSCATION_TOOLKIT';

prompt ==================== [4] DB/국가 문자셋 ====================
select parameter, value from nls_database_parameters
 where parameter in ('NLS_CHARACTERSET','NLS_NCHAR_CHARACTERSET');

prompt ==================== [5] CLASSES 표본: 저장바이트 / 복호바이트(hex) ====================
prompt   -- STORED = 컬럼 원본 바이트, DEC_* = 각 후보식의 복호 결과 바이트(hex). ERR 이면 그 식은 실패.
prompt   -- 정답 후보의 DEC 결과를 CP949 로 디코드하면 한글/영문이 나와야 한다.
DECLARE
  TYPE tcols IS TABLE OF VARCHAR2(30);
  cols  tcols := tcols('CLASS_NAME','CLASS_NAME_K','CLASS_NAME_ISUS90');
  v_owner VARCHAR2(30);
  v_hex   VARCHAR2(4000);
  v_rowid ROWID;

  FUNCTION safe(sql_expr VARCHAR2) RETURN VARCHAR2 IS
    v VARCHAR2(4000);
  BEGIN
    EXECUTE IMMEDIATE
      'SELECT RAWTOHEX(UTL_RAW.CAST_TO_RAW('||sql_expr||')) FROM CLASSES WHERE ROWID = :r'
      INTO v USING v_rowid;
    RETURN NVL(v,'(null)');
  EXCEPTION WHEN OTHERS THEN RETURN 'ERR:'||SQLCODE;
  END;
BEGIN
  -- 로그온 스키마의 CLASSES 를 대상으로, 이름 비어있지 않은 앞 3행을 본다.
  FOR r IN (SELECT ROWID rid, class_id FROM CLASSES WHERE ROWNUM <= 3) LOOP
    v_rowid := r.rid;
    dbms_output.put_line('── CLASS_ID='||r.class_id||' ─────────────────────────');
    FOR i IN 1..cols.COUNT LOOP
      dbms_output.put_line(rpad(cols(i),20)||' STORED   = '||safe(cols(i)));
      -- 후보 A: 컬럼을 그대로 VARCHAR2 로 보고 복호 (RAW→문자 캐스팅)
      dbms_output.put_line(rpad(' ',20)||' DEC_A    = '||safe('CRYPTO.DECRYPT(UTL_RAW.CAST_TO_VARCHAR2('||cols(i)||'))'));
      -- 후보 C: 컬럼이 이미 VARCHAR2 라면 (RAW 캐스팅 없이) 직접 복호
      dbms_output.put_line(rpad(' ',20)||' DEC_C    = '||safe('CRYPTO.DECRYPT('||cols(i)||')'));
      -- 후보 H: RAW 안에 hex 텍스트가 든 경우 → HEXTORAW 로 실제 암호문 복원 후 복호
      dbms_output.put_line(rpad(' ',20)||' DEC_H    = '||safe('CRYPTO.DECRYPT(UTL_RAW.CAST_TO_VARCHAR2(HEXTORAW(UTL_RAW.CAST_TO_VARCHAR2('||cols(i)||'))))'));
    END LOOP;
  END LOOP;
END;
/

prompt ==================== [6] 길이 분포(멀티바이트 패딩 버그 흔적) ====================
prompt   -- 저장 바이트길이가 8의 배수가 아니면 classic DES 암호문이 아님(=평문/hex텍스트 가능성).
select 'CLASS_NAME'   col, count(*) n,
       sum(case when mod(lengthb(utl_raw.cast_to_varchar2(class_name)),8)=0 then 1 else 0 end) mult8_ok
  from classes where class_name is not null
union all
select 'CLASS_NAME_K', count(*),
       sum(case when mod(lengthb(utl_raw.cast_to_varchar2(class_name_k)),8)=0 then 1 else 0 end)
  from classes where class_name_k is not null;

prompt ==================== 진단 끝 ====================
