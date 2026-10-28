param(
  [string]$Dump      = "",              # .dmp 경로 (비우면 ..\dump\legacynaims.dmp)
  [string]$SrcSchema = "",              # 원본 스키마 (비우면 대상과 동일 가정; 현장 full 덤프면 보통 SYSTEM)
  [string]$TgtSchema = "LEGACYNAIMS"    # 재적재 대상 스키마
)
# ============================================================================
#  04b_restore_anydump.ps1   -  ANY Oracle dump restore (auto classic/Data Pump)
#  04_restore_dump.ps1 이 impdp 전용이라, 현장 실덤프가 classic exp 면 실패한다.
#  이 스크립트는 파일 헤더로 종류를 자동 판별해 imp / impdp 를 알맞게 실행한다.
#  Output is ASCII/English so it never mojibakes.
#    powershell -ExecutionPolicy Bypass -File scripts\04b_restore_anydump.ps1
#    powershell -ExecutionPolicy Bypass -File scripts\04b_restore_anydump.ps1 -Dump D:\field.dmp -SrcSchema SYSTEM
# ============================================================================
try { chcp 65001 > $null } catch {}
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$OH  = "D:\app\oracle\product\12.2.0\dbhome_1"
. "$PSScriptRoot\_secrets.ps1"   # 비밀번호는 코드에 두지 않는다 (환경변수 → 파라미터 → 화면 입력)
$SID = "NAIMS12"; $PW = Get-NaimsSecret -EnvName 'NAIMS12_SYS_PW' -Prompt 'NAIMS12 SYSTEM 비밀번호' -DefaultValue 'Naims12#2026'
$dba = "system/$PW@localhost:1522/$SID"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if(-not $Dump){ $Dump = Join-Path (Split-Path -Parent $here) "dump\legacynaims.dmp" }
if(-not $SrcSchema){ $SrcSchema = $TgtSchema }

Write-Host "===== 04b restore =====  dump=$Dump  src=$SrcSchema  tgt=$TgtSchema"
if(-not (Test-Path "$OH\bin\sqlplus.exe")){ Write-Host "[ABORT] Oracle home not usable. Run 00 (and 98_diagnose)."; return }
if(-not (Test-Path $Dump)){ Write-Host "[ABORT] dump not found: $Dump"; return }

# 1) DB reachable?
$open = (@"
set head off feed off pages 0
select open_mode from v`$database;
exit
"@ | & "$OH\bin\sqlplus.exe" -S -L $dba 2>&1 | Out-String)
if($open -notmatch 'READ WRITE'){ Write-Host "[ABORT] NAIMS12 not OPEN. Run 01 first. ($($open.Trim()))"; return }

# 2) detect kind from header (classic exp starts with ascii 'EXPORT:V')
$fs = [System.IO.File]::OpenRead($Dump)
$buf = New-Object byte[] 512; $n = $fs.Read($buf,0,512); $fs.Close()
$ascii = [System.Text.Encoding]::ASCII.GetString($buf,0,$n)
$isClassic = $ascii.Contains("EXPORT:V")
Write-Host ("Detected: " + $(if($isClassic){"classic exp  -> imp.exe"}else{"Data Pump   -> impdp.exe"}))

$logFile = [System.IO.Path]::GetFileNameWithoutExtension($Dump) + "_imp.log"

if($isClassic){
  # ---- classic imp (client-side file; no directory object needed) ----
  $imp = "$OH\bin\imp.exe"
  if(-not (Test-Path $imp)){ Write-Host "[ABORT] imp.exe not found."; return }
  $logPath = Join-Path (Split-Path -Parent $Dump) $logFile
  Write-Host "Running imp  fromuser=$SrcSchema touser=$TgtSchema ..."
  & $imp "$dba" file="$Dump" fromuser=$SrcSchema touser=$TgtSchema log="$logPath" ignore=y
  $code = $LASTEXITCODE
} else {
  # ---- Data Pump impdp (server-side; needs a DIRECTORY object) ----
  $impdp = "$OH\bin\impdp.exe"
  if(-not (Test-Path $impdp)){ Write-Host "[ABORT] impdp.exe not found."; return }
  $dir = Split-Path -Parent $Dump
  $file = Split-Path -Leaf $Dump
  Write-Host "Creating DIRECTORY MIG_RESTORE_DIR -> $dir"
  @"
CREATE OR REPLACE DIRECTORY MIG_RESTORE_DIR AS '$dir';
exit
"@ | & "$OH\bin\sqlplus.exe" -S $dba | Out-Null
  if($SrcSchema -ieq $TgtSchema){ $schemaArg = "schemas=$TgtSchema" }
  else { $schemaArg = "schemas=$SrcSchema remap_schema=$SrcSchema`:$TgtSchema" }
  Write-Host "Running impdp $schemaArg ..."
  & $impdp "$dba" directory=MIG_RESTORE_DIR dumpfile="$file" logfile="$logFile" $schemaArg.Split(' ') table_exists_action=replace
  $code = $LASTEXITCODE
}

Write-Host "Tool exit code = $code  (0 = clean, 5 = completed with harmless warnings e.g. schema exists)"

# 3) verify: table count in target schema
$cnt = (@"
set head off feed off pages 0
select 'TBL=' || count(*) from dba_tables where owner=upper('$TgtSchema');
exit
"@ | & "$OH\bin\sqlplus.exe" -S -L $dba 2>&1 | Out-String)
Write-Host ("Verify: " + (($cnt -split "`n" | Where-Object {$_ -match 'TBL='}) -join ''))
if($code -eq 0 -or $code -eq 5){ Write-Host "[DONE] restore finished. Next: 05_register_dsn.ps1 -> migrator." }
else { Write-Host "[CHECK] non-standard exit. See log next to the dump: $logFile" }
