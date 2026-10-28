# ============================================================================
#  11_transplant_oracle_home.ps1   *** RUN ON THE W/S, AS ADMINISTRATOR ***
#  Install Oracle 12c WITHOUT running the installer, by restoring a known-good
#  home exported from a working PC (see 10_export_oracle_home.ps1).
#
#  Use this when the W/S installer keeps producing a broken home
#  ("Error 57 initializing SQL*Plus" even after a full clean reinstall).
#
#  Steps: clean leftovers -> extract dbhome_1.zip to the SAME path -> import
#         registry (home registration + ODBC driver) -> verify with sqlplus -V.
#  Output is ASCII/English.
# ============================================================================
param(
  [string]$Image      = "",                                        # folder with dbhome_1.zip + oracle_home.reg
  [string]$OracleHome = "D:\app\oracle\product\12.2.0\dbhome_1",   # MUST match the source path
  [switch]$KeepExisting                                            # skip the cleanup step
)
try { chcp 65001 > $null } catch {}
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$pr=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){ Write-Host "[ABORT] Administrator rights required."; return }

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if(-not $Image){ $Image = Join-Path (Split-Path -Parent $here) "home_image" }
$zip = Join-Path $Image "dbhome_1.zip"
$reg = Join-Path $Image "oracle_home.reg"
if(-not (Test-Path $zip)){ Write-Host "[ABORT] not found: $zip  (run 10_export_oracle_home.ps1 on the working PC first)"; return }

Write-Host "===== [1/5] Space check ====="
$drv = $OracleHome.Substring(0,1)
$free = (Get-PSDrive $drv -EA SilentlyContinue).Free
Write-Host ("  ${drv}: free = " + [math]::Round($free/1GB,1) + " GB (need ~7 GB for the extracted home)")
if($free -lt 7GB){ Write-Host "  [STOP] not enough free space."; return }

if(-not $KeepExisting){
  Write-Host "===== [2/5] Clean previous Oracle (services / home / inventory / registry) ====="
  Get-Service Oracle* -EA SilentlyContinue | ForEach-Object { & sc.exe stop $_.Name 2>$null|Out-Null; & sc.exe delete $_.Name 2>$null|Out-Null }
  Start-Sleep 3
  Get-Process oracle,tnslsnr -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
  if(Test-Path $OracleHome){ try{ Remove-Item $OracleHome -Recurse -Force -EA Stop; Write-Host "  removed old home" }catch{ Write-Host ("  [WARN] " + $_.Exception.Message) } }
  $invReg = (Get-ItemProperty 'HKLM:\SOFTWARE\Oracle' -Name inst_loc -EA SilentlyContinue).inst_loc
  foreach($iv in @($invReg,"C:\Program Files\Oracle\Inventory","C:\Program Files (x86)\Oracle\Inventory") | Where-Object {$_} | Select-Object -Unique){
    if(Test-Path $iv){ Remove-Item $iv -Recurse -Force -EA SilentlyContinue; Write-Host "  removed inventory: $iv" }
  }
  foreach($k in 'HKLM:\SOFTWARE\Oracle','HKLM:\SOFTWARE\Wow6432Node\Oracle'){ if(Test-Path $k){ Remove-Item $k -Recurse -Force -EA SilentlyContinue } }
} else { Write-Host "===== [2/5] (skipped cleanup: -KeepExisting) =====" }

Write-Host "===== [3/5] Extract the home image (several minutes, ~5-6 GB) ====="
$parent = Split-Path -Parent $OracleHome
New-Item -ItemType Directory -Force -Path $parent | Out-Null
try {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $za=[System.IO.Compression.ZipFile]::OpenRead($zip)
  $total=$za.Entries.Count; $n=0; $skipped=0; $t0=Get-Date
  New-Item -ItemType Directory -Force -Path $OracleHome | Out-Null
  foreach($e in $za.Entries){
    if([string]::IsNullOrEmpty($e.Name)){ continue }                       # directory entry
    if($e.FullName -match '^%|^[A-Za-z]:|\.\.'){ $skipped++; continue }    # unsafe/absolute path (e.g. %systemroot%\...)
    $dest = Join-Path $OracleHome $e.FullName
    $dir  = Split-Path -Parent $dest
    if(-not (Test-Path $dir)){ New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    try { [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e,$dest,$true); $n++ }
    catch { $skipped++ }
    if($n % 5000 -eq 0){ Write-Host ("    ... $n / $total files, " + [int]((Get-Date)-$t0).TotalSeconds + "s") }
  }
  $za.Dispose()
  Write-Host ("  extracted $n files to $OracleHome (skipped $skipped)")
} catch { Write-Host ("[ABORT] extract failed: " + $_.Exception.Message); return }

Write-Host "===== [4/5] Import registry (home registration + ODBC driver) ====="
if(Test-Path $reg){ & reg.exe import $reg 2>&1 | Out-Null; Write-Host "  imported $reg" }
else { Write-Host "  [WARN] $reg missing - ODBC driver may not be registered (05b_fix_dsn.ps1 can help)" }
# inventory pointer so future Oracle tools do not get confused
New-Item -Path 'HKLM:\SOFTWARE\Oracle' -Force | Out-Null
Set-ItemProperty 'HKLM:\SOFTWARE\Oracle' -Name inst_loc -Value "C:\Program Files\Oracle\Inventory"

Write-Host "===== [5/5] Verify ====="
$env:ORACLE_HOME=$OracleHome; $env:PATH="$OracleHome\bin;$env:PATH"
$sp = Join-Path $OracleHome "bin\sqlplus.exe"
$v = $(if(Test-Path $sp){ (& $sp -V 2>&1 | Out-String).Trim() } else {''})
if($v -notmatch 'Release' -and (Test-Path $sp)){
  # SELF-HEAL: make sure the transplanted home is registered, then retry.
  Write-Host "  sqlplus -V failed - ensuring the home is registered..."
  $s13 = Join-Path $here "13_register_home.ps1"
  if(Test-Path $s13){ & powershell -ExecutionPolicy Bypass -File $s13 -OracleHome $OracleHome | ForEach-Object { Write-Host ("    " + $_) } }
  $v = (& $sp -V 2>&1 | Out-String).Trim()
}
if($v -match 'Release'){
  Write-Host ("[SUCCESS] transplanted home works - " + (($v -split "`n" | Where-Object {$_ -match 'Release'} | Select-Object -First 1).Trim()))
  Write-Host "  NEXT: powershell -ExecutionPolicy Bypass -File scripts\01_install_and_createdb_ELEVATED.ps1"
  Write-Host "        then 02/03 -> 04 -> 05 -> 99_verify"
} else {
  Write-Host "[FAIL] sqlplus still does not run after transplant."
  if($v){ Write-Host ("  " + ($v -replace "`r?`n"," ")) }
  Write-Host "  Check: the home MUST be at the same path as the source ($OracleHome),"
  Write-Host "         and the extract must have completed (compare with 98_diagnose.ps1 baseline diff)."
}
