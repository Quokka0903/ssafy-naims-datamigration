# classic exp 로컬 덤프 (현장 backup.bat 형태 리허설용)

로컬 리그(`LEGACYNAIMS`, 42테이블)를 **현장 backup.bat 과 동일한 classic exp 형태**로 뽑은 덤프.
목적: 도구의 **classic 다중 .DMP 복구 경로**(폴더 일괄 imp + fromuser/touser + 재실행정리)를 오프라인 W/S에서 리허설.

## 왜 스테이징(LEGACYEXP)을 거치나
로컬 리그는 MsSQL→Oracle 재제작 산물이라 `float→BINARY_DOUBLE`, `real→BINARY_FLOAT` 로 만들어졌다.
**classic exp 는 BINARY_DOUBLE/FLOAT 를 못 뜬다(EXP-00104).** 그래서 `01_build_staging.sql` 이
그 컬럼만 `NUMBER` 로 CAST 한 **같은 이름의 스테이징 스키마 LEGACYEXP** 를 만들고, 그걸 exp 한다.
(현장 원본은 USPASKDB.txt대로 `NUMBER` 라 스테이징 없이 바로 classic exp 가능 — 로컬만의 우회다.)

## 구성
```
dump\OracleTable_<TABLE>.DMP   ... 테이블별 classic 덤프 42개 (+ .exp.log)
01_build_staging.sql           ... LEGACYEXP 스테이징 생성(재실행 안전)
run_classic_export.ps1         ... 스테이징 빌드 + bat 생성 + exp 일괄 실행(재생성용)
backup_classic.bat             ... 생성된 EXP 명령(현장 backup.bat 형태, 소유자만 LEGACYEXP)
recovery_classic.bat           ... 대응 IMP 명령(참고용 — 실제 복구는 도구로)
```

## W/S에서 복구 리허설 (도구 사용)
1. 도구 `DbToDb` → **💾 덤프 복구** 패널
2. **📁 폴더** → 이 `dump\` 폴더 지정 (42개 자동 인식, classic 자동 판별)
3. DBA 접속: `system/<대상DB비번>@localhost:1522/NAIMS12`
4. 스키마: **원본=`LEGACYEXP`**, **대상=`LEGACYNAIMS`**
5. **재실행정리 = DROP** (dropdb.txt 대응 — 재실행 멱등 확인 시)
6. **덤프 복구 실행** → 로그에서 42개 imp 순차 완료 확인
7. 이어서 **[연결 시험] → [Oracle 암호화 추출](CRYPTO 심기) → 시험 실행**

> ⚠️ 복구된 스테이징 데이터는 이름컬럼 암호화 상태가 원본과 동일하다(RAW 그대로 복제).
> CRYPTO 패키지/SYNONYM 은 덤프에 없으므로 복구 후 반드시 암호화 추출 단계를 거쳐야 이름 복호가 된다.

## 로컬에서 다시 만들려면
```powershell
.\run_classic_export.ps1
```
(옵션: `-Conn`, `-OutDir`, `-OracleHome`)

## 정리(스테이징 제거)
필요 없어지면 로컬 DB에서:
```sql
DROP USER LEGACYEXP CASCADE;
```
