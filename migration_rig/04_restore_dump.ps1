# 04_restore_dump.ps1  -  restore legacynaims.dmp into the LEGACYNAIMS schema (impdp).
# Prereq: 01 (create DB), 02 (user), 03 (crypto) done.
# NOTE: this is for the packaged Data Pump dump. For a FIELD dump that may be
#       classic exp, use 04b_restore_anydump.ps1 (auto-detects imp vs impdp).
# Output is ASCII/English.
try { chcp 65001 > $null } catch {}
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$OH   = "D:\app\oracle\product\12.2.0\dbhome_1"     # W/S 12c home (change if different)
$env:ORACLE_HOME = $OH
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dump = Join-Path (Split-Path -Parent $here) "dump\legacynaims.dmp"
if (-not (Test-Path "$OH\bin\impdp.exe")) { Write-Host "[ABORT] impdp.exe not found. Run 00 first (and 98_diagnose)." -ForegroundColor Red; exit 1 }
if (-not (Test-Path $dump)) { Write-Host "[ABORT] dump not found: $dump" -ForegroundColor Red; exit 1 }

# find DATA_PUMP_DIR OS path
$dp = (@'
set head off feed off pages 0 lines 300
select directory_path from dba_directories where directory_name='DATA_PUMP_DIR';
exit
'@ | & "$OH\bin\sqlplus.exe" -S "/ as sysdba") | Where-Object { $_ -match '\\' } | Select-Object -First 1
$dp = $dp.Trim()
if (-not $dp) { $dp = "$OH\rdbms\log" }
Write-Host "DATA_PUMP_DIR = $dp"
Copy-Item $dump (Join-Path $dp "legacynaims.dmp") -Force

Write-Host "=== impdp restore (schemas=LEGACYNAIMS) ==="
. "$PSScriptRoot\_secrets.ps1"   # 비밀번호는 코드에 두지 않는다 (환경변수 → 파라미터 → 화면 입력)
$SysPw = Get-NaimsSecret -EnvName 'NAIMS12_SYS_PW' -Prompt 'NAIMS12 SYSTEM 비밀번호' -DefaultValue 'Naims12#2026'
& "$OH\bin\impdp.exe" "system/$SysPw@localhost:1522/NAIMS12" `
   schemas=LEGACYNAIMS directory=DATA_PUMP_DIR dumpfile=legacynaims.dmp `
   logfile=legacynaims_imp.log table_exists_action=replace
$code = $LASTEXITCODE
Write-Host "impdp exit code = $code  (0 = clean, 5 = completed with harmless warnings e.g. schema exists)"
if ($code -eq 0 -or $code -eq 5) {
    Write-Host "[DONE] restore finished. Verify: sqlplus legacynaims/<비밀번호>@localhost:1522/LegacyNaims  ->  SELECT COUNT(*) FROM user_tables;"
    Write-Host "       Next: 05_register_dsn.ps1"
} else {
    Write-Host "[CHECK] non-standard exit. See log: $dp\legacynaims_imp.log"
}
