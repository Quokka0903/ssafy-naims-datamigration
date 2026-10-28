# ============================================================================
#  99_fix_reinstall.ps1   *** RUN AS ADMINISTRATOR ***
#  DEFINITIVE hard reset + reinstall for a FAILED / INCOMPLETE / CONFLICTING
#  Oracle 12c software install.
#  Symptoms it fixes:
#    - "Unable to load Oracle dll's" / "Error 57 initializing SQL*Plus"
#    - "[INS-32025] conflicts with software already installed in the Oracle home"
#    - 98_diagnose shows ONLY 'free space' OK = nothing actually installed
#      (setup.exe returns 0 but installs nothing because the OLD home is still
#       recorded in the Central Inventory + registry - deleting the folder is
#       NOT enough).
#  It removes ALL Oracle services, the home, the base, the Central Inventory
#  (at every known location), and the Oracle registry (both nodes), then
#  reinstalls (00) and verifies. If it still fails, it prints the OUI INS-error
#  lines so you know exactly why. Output is ASCII/English.
# ============================================================================
try { chcp 65001 > $null } catch {}
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$pr=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){ Write-Host "[ABORT] Administrator rights required."; return }

$OH="D:\app\oracle\product\12.2.0\dbhome_1"; $BASE="D:\app\oracle"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "===== [1/7] Stop and DELETE all Oracle services ====="
Get-Service Oracle* -EA SilentlyContinue | ForEach-Object {
  Write-Host ("  {0}: stop+delete" -f $_.Name)
  & sc.exe stop   $_.Name 2>$null | Out-Null
  & sc.exe delete $_.Name 2>$null | Out-Null
}
Start-Sleep 3
Get-Process oracle,tnslsnr,perl -EA SilentlyContinue | Where-Object { $_.Path -like "$BASE*" -or $_.Path -like "D:\OraInstall*" } | Stop-Process -Force -EA SilentlyContinue

Write-Host "===== [2/7] Remove Oracle home + base ====="
foreach($p in @($OH,$BASE)){
  if(Test-Path $p){
    try { Remove-Item $p -Recurse -Force -EA Stop; Write-Host "  removed: $p" }
    catch { Write-Host "  [WARN] could not fully remove $p : $($_.Exception.Message)"; Write-Host "         close Explorer/Oracle processes on it and re-run." }
  }
}

Write-Host "===== [3/7] Remove Central Inventory at EVERY known location (fixes INS-32025) ====="
$invReg = (Get-ItemProperty 'HKLM:\SOFTWARE\Oracle' -Name inst_loc -EA SilentlyContinue).inst_loc
$invLocs = @($invReg,
  "C:\Program Files\Oracle\Inventory",
  "C:\Program Files (x86)\Oracle\Inventory",
  "D:\app\oraInventory",
  "$BASE\oraInventory") | Where-Object { $_ } | Select-Object -Unique
foreach($iv in $invLocs){
  if(Test-Path $iv){
    try { Remove-Item $iv -Recurse -Force -EA Stop; Write-Host "  removed inventory: $iv" }
    catch { Write-Host "  [WARN] could not remove $iv : $($_.Exception.Message)" }
  }
}
if(-not ($invLocs | Where-Object { Test-Path $_ })){ Write-Host "  inventory clear." }

Write-Host "===== [4/7] Remove Oracle registry keys (home registration, both nodes) ====="
foreach($k in 'HKLM:\SOFTWARE\Oracle','HKLM:\SOFTWARE\Wow6432Node\Oracle'){
  if(Test-Path $k){ try { Remove-Item $k -Recurse -Force -EA Stop; Write-Host "  removed: $k" } catch { Write-Host "  [WARN] $k : $($_.Exception.Message)" } }
}

Write-Host "===== [5/7] Disk check ====="
$d = Get-PSDrive D -EA SilentlyContinue
$free = $(if($d){[math]::Round($d.Free/1GB,1)}else{0})
Write-Host "  D: free = $free GB (need 15GB+)"
if($d -and $free -lt 15){ Write-Host "  [STOP] Not enough free space on D:. Free up space, then re-run."; return }

Write-Host "===== [6/7] Re-run software install (00) ====="
$s00 = Join-Path $here "00_install_software_ELEVATED.ps1"
if(-not (Test-Path $s00)){ Write-Host "[ABORT] $s00 not found (run this from the scripts folder)."; return }
& powershell -ExecutionPolicy Bypass -File $s00

Write-Host "===== [7/7] Verify + explain if still failing ====="
$verTxt = $(if(Test-Path "$OH\bin\sqlplus.exe"){ (& "$OH\bin\sqlplus.exe" -V 2>&1 | Out-String).Trim() } else {''})
if($verTxt -notmatch 'Release' -and (Test-Path "$OH\bin\sqlplus.exe")){
  # SELF-HEAL: this script deletes HKLM\SOFTWARE\Oracle during cleanup. If the
  # reinstall did not recreate the home registration, sqlplus fails with "Error 57".
  Write-Host "  sqlplus -V failed - ensuring the home is registered..."
  $s13 = Join-Path $here "13_register_home.ps1"
  if(Test-Path $s13){ & powershell -ExecutionPolicy Bypass -File $s13 -OracleHome $OH | ForEach-Object { Write-Host ("    " + $_) } }
  $verTxt = (& "$OH\bin\sqlplus.exe" -V 2>&1 | Out-String).Trim()
}
if($verTxt -match 'Release'){
  Write-Host "===== [SUCCESS] Oracle software is now usable ====="
  Write-Host ("  " + (($verTxt -split "`n" | Where-Object {$_ -match 'Release'} | Select-Object -First 1).Trim()))
  Write-Host "  NEXT: powershell -ExecutionPolicy Bypass -File scripts\01_install_and_createdb_ELEVATED.ps1"
} else {
  Write-Host "===== [STILL FAILING] sqlplus -V did not print a version ====="
  Write-Host "  Reason lines from the OUI install log:"
  $log = Get-ChildItem "C:\Program Files\Oracle\Inventory\logs\installActions*.log" -EA SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
  if($log){
    Select-String $log.FullName -Pattern "INS-\d+","FATAL","Successfully Setup" | Select-Object -Last 12 | ForEach-Object { Write-Host ("    " + $_.Line.Trim()) }
  } else { Write-Host "    (no OUI log found - setup.exe may not have run; check D:\OraInstall\db12201\database\setup.exe exists)" }
  Write-Host "  If you STILL see INS-32025 after this full clean, install to a FRESH home instead:"
  Write-Host "    1) edit db_install_naims12.rsp: ORACLE_HOME=D:\ora12\dbhome1  ORACLE_BASE=D:\ora12"
  Write-Host "    2) run 00, then 01 with:  -OracleHome D:\ora12\dbhome1 -OracleBase D:\ora12"
}
