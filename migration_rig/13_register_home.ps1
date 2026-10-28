# ============================================================================
#  13_register_home.ps1   *** RUN AS ADMINISTRATOR ***
#  Fix "Error 57 initializing SQL*Plus" when every file is present and PATH is
#  correct, but the Oracle home is NOT REGISTERED.
#
#  Why this happens: when the ORACLE_HOME environment variable is not set,
#  SQL*Plus locates its home (and its message files) through the registry key
#     HKLM\SOFTWARE\ORACLE\KEY_<HomeName>\ORACLE_HOME
#  A hard clean (99_fix_reinstall) deletes HKLM\SOFTWARE\Oracle. If the
#  reinstall did not recreate it, sqlplus has no home to resolve -> Error 57
#  ("Error loading message shared library"), even though sqlplus.exe exists.
#
#  This script re-registers the home:
#     - imports home_image\oracle_home.reg if present (exact copy from a good PC)
#     - otherwise recreates the minimum keys itself
#     - sets a machine-wide ORACLE_HOME as a belt-and-braces fallback
#     - verifies with 'sqlplus -V'
#  Output is ASCII/English.
# ============================================================================
param(
  [string]$OracleHome = "D:\app\oracle\product\12.2.0\dbhome_1",
  [string]$HomeName   = "OraDB12Home1"
)
try { chcp 65001 > $null } catch {}
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$pr=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){ Write-Host "[ABORT] Administrator rights required (writes HKLM)."; return }
if(-not (Test-Path (Join-Path $OracleHome 'bin\sqlplus.exe'))){ Write-Host "[ABORT] sqlplus.exe not found under $OracleHome - install/transplant first."; return }

Write-Host "===== [1/4] Current registration ====="
$key = "HKLM:\SOFTWARE\ORACLE\KEY_$HomeName"
$cur = (Get-ItemProperty $key -Name ORACLE_HOME -EA SilentlyContinue).ORACLE_HOME
Write-Host ("  $key -> " + $(if($cur){$cur}else{'(missing)'}))
$envH = [Environment]::GetEnvironmentVariable('ORACLE_HOME','Machine')
Write-Host ("  ORACLE_HOME (machine)  -> " + $(if($envH){$envH}else{'(not set)'}))

Write-Host "===== [2/4] Re-register the home ====="
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$reg  = Join-Path (Split-Path -Parent $here) "home_image\oracle_home.reg"
$imported = $false
if(Test-Path $reg){
  # only safe if the image came from the same path
  $txt = Get-Content $reg -Raw
  if($txt -match [regex]::Escape($OracleHome.Replace('\','\\'))){
    & reg.exe import $reg 2>&1 | Out-Null
    Write-Host "  imported $reg"
    $imported = $true
  } else {
    Write-Host "  [SKIP] $reg was exported from a different path - not importing (would point to the wrong home)."
  }
}
if(-not $imported){
  New-Item -Path $key -Force | Out-Null
  Set-ItemProperty $key -Name ORACLE_HOME      -Value $OracleHome
  Set-ItemProperty $key -Name ORACLE_HOME_NAME -Value $HomeName
  Set-ItemProperty $key -Name ORACLE_HOME_KEY  -Value "SOFTWARE\ORACLE\KEY_$HomeName"
  Set-ItemProperty $key -Name ORACLE_HOME_TYPE -Value "1"
  Set-ItemProperty $key -Name NLS_LANG         -Value "KOREAN_KOREA.KO16MSWIN949"
  Write-Host "  recreated $key"
}
# inventory pointer (harmless if already right)
New-Item -Path 'HKLM:\SOFTWARE\Oracle' -Force | Out-Null
Set-ItemProperty 'HKLM:\SOFTWARE\Oracle' -Name inst_loc -Value "C:\Program Files\Oracle\Inventory"

Write-Host "===== [3/4] Set machine-wide ORACLE_HOME (fallback) ====="
[Environment]::SetEnvironmentVariable('ORACLE_HOME',$OracleHome,'Machine')
Write-Host "  ORACLE_HOME = $OracleHome   (new cmd windows will see it)"

Write-Host "===== [4/4] Verify ====="
$env:ORACLE_HOME=$OracleHome; $env:PATH="$OracleHome\bin;$env:PATH"
$v = (& (Join-Path $OracleHome 'bin\sqlplus.exe') -V 2>&1 | Out-String).Trim()
if($v -match 'Release'){
  Write-Host ("[SUCCESS] " + (($v -split "`n" | Where-Object {$_ -match 'Release'} | Select-Object -First 1).Trim()))
  Write-Host "  The home was simply not registered. No reinstall was needed."
  Write-Host "  NEXT: powershell -ExecutionPolicy Bypass -File scripts\01_install_and_createdb_ELEVATED.ps1"
} else {
  Write-Host "[STILL FAILING] sqlplus -V:"
  Write-Host ("  " + ($v -replace "`r?`n"," "))
  Write-Host "  Open a NEW cmd window and try again (env changes need a fresh process)."
  Write-Host "  If it still fails, run scripts\98_diagnose.ps1 and compare against baseline.json."
}
