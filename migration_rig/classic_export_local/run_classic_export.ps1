# ════════════════════════════════════════════════════════════════════════
#  run_classic_export.ps1
#  로컬 리그(LEGACYNAIMS)를 현장 backup.bat 과 "동일한 형태"의 classic exp 덤프로 만든다.
#    1) 01_build_staging.sql 실행 → LEGACYEXP(숫자형 NUMBER 치환) 생성
#    2) LEGACYEXP 테이블 목록을 조회해 backup_classic.bat / recovery_classic.bat 생성(backup.bat 형태)
#    3) 테이블별 classic exp → <OutDir>\OracleTable_<TABLE>.DMP
#  이렇게 나온 폴더를 도구의 [📁 폴더] 로 지정하면 classic 다중 imp 경로가 그대로 리허설된다.
#
#  ※ 스테이징 소유자 = LEGACYEXP → 복구 시 앱에서 원본 스키마=LEGACYEXP, 대상=LEGACYNAIMS 로.
# ════════════════════════════════════════════════════════════════════════
param(
  [string]$OracleHome = 'D:\app\oracle\product\12.2.0\dbhome_1',
  # 비밀번호를 넘기지 않으면 $env:NAIMS12_SYS_PW 또는 화면 입력으로 채운다(_secrets.ps1).
  [string]$Conn       = '',
  [string]$OutDir     = 'D:\OraInstall\classic_dump_out'
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\_secrets.ps1"   # 비밀번호는 코드에 두지 않는다
if (-not $Conn) {
  $sysPw = Get-NaimsSecret -EnvName 'NAIMS12_SYS_PW' -Prompt 'NAIMS12 SYSTEM 비밀번호' -DefaultValue 'Naims12#2026'
  $Conn  = "system/$sysPw@localhost:1522/NAIMS12"
}
$env:ORACLE_HOME = $OracleHome
$env:PATH        = "$OracleHome\bin;$env:PATH"
$env:NLS_LANG    = 'KOREAN_KOREA.KO16MSWIN949'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$sqlplus = Join-Path $OracleHome 'bin\sqlplus.exe'
$exp     = Join-Path $OracleHome 'bin\exp.exe'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Write-Host "=== [1/3] 스테이징(LEGACYEXP) 빌드 ===" -ForegroundColor Cyan
& $sqlplus -S "$Conn" "@$(Join-Path $scriptDir '01_build_staging.sql')"
if ($LASTEXITCODE -ne 0) { throw "스테이징 빌드 실패(exit $LASTEXITCODE)" }

Write-Host "=== [2/3] 테이블 목록 조회 + backup/recovery bat 생성 ===" -ForegroundColor Cyan
$listSql = @"
set pagesize 0 feedback off heading off linesize 200 trimspool on
select table_name from all_tables where owner='LEGACYEXP' order by table_name;
exit
"@
$tables = ($listSql | & $sqlplus -S "$Conn") |
  ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^[A-Za-z0-9_$#]+$' }

if (-not $tables -or $tables.Count -eq 0) { throw "LEGACYEXP 테이블을 찾지 못했습니다." }
Write-Host ("   테이블 {0}개" -f $tables.Count)

# backup.bat 형태(현장 원본과 동일한 모양). 소유자만 LEGACYEXP.
$bak = foreach ($t in $tables) {
  "EXP USERID = $Conn FILE = $OutDir\OracleTable_$t.DMP TABLES = ( LEGACYEXP.$t )`r`n"
}
$rec = foreach ($t in $tables) {
  "IMP USERID = $Conn FILE = $OutDir\OracleTable_$t.DMP FROMUSER = LEGACYEXP TOUSER = LEGACYNAIMS TABLES = ( $t )`r`n"
}
Set-Content -Path (Join-Path $scriptDir 'backup_classic.bat')   -Value $bak -Encoding OEM
Set-Content -Path (Join-Path $scriptDir 'recovery_classic.bat') -Value $rec -Encoding OEM

Write-Host "=== [3/3] 테이블별 classic exp 실행 → $OutDir ===" -ForegroundColor Cyan
$fail = 0
foreach ($t in $tables) {
  $dmp = Join-Path $OutDir "OracleTable_$t.DMP"
  $log = Join-Path $OutDir "OracleTable_$t.exp.log"
  & $exp "$Conn" FILE="$dmp" TABLES="(LEGACYEXP.$t)" LOG="$log" STATISTICS=NONE | Out-Null
  if ($LASTEXITCODE -gt 1) { Write-Host ("   ERR {0} (exit {1})" -f $t, $LASTEXITCODE) -ForegroundColor Red; $fail++ }
  else                     { Write-Host ("   OK  {0}" -f $t) -ForegroundColor DarkGray }
}

$dmps = Get-ChildItem $OutDir -Filter 'OracleTable_*.DMP' -ErrorAction SilentlyContinue
Write-Host ("=== 완료: .DMP {0}개, 실패 {1}개 → {2} ===" -f $dmps.Count, $fail, $OutDir) -ForegroundColor Green
Write-Host "복구 테스트: 도구 [📁 폴더]=$OutDir, 원본=LEGACYEXP, 대상=LEGACYNAIMS, 재실행정리=DROP" -ForegroundColor Yellow
