# ============================================================================
#  14_install_vcredist.ps1   *** RUN AS ADMINISTRATOR ***
#  Install the Visual C++ 2013 x64 runtime that Oracle 12.2 REQUIRES.
#
#  Why this matters:
#    sqlplus.exe imports MSVCR120.dll, which comes from the "Visual C++ 2013
#    Redistributable (x64)". Without that runtime the Oracle install can still
#    report SUCCESS while nothing runs:
#        "Error 57 initializing SQL*Plus"
#        "Error loading message shared library"
#
#    NOTE (verified in installActions.log of a working install): OUI normally
#    installs this itself - it sets MSVCREDIST_INSTALL=true and runs
#        %TEMP%\OraInstall<date>\ext\bin\vcredist_x64.exe /q /norestart
#    So the media is NOT missing anything. On some machines that step simply does
#    not take effect (temp extraction blocked, AV, silent failure, reboot pending),
#    and the result is a "successful" install that cannot run.
#    This script makes the step explicit and verifiable.
#
#  Good news: vcredist_x64.exe ships INSIDE the Oracle media
#    <staging>\database\stage\ext\vcredist_x64.exe
#  so this works fully offline.
#
#  This script: detect -> install silently -> verify -> re-test sqlplus.
#  Output is ASCII/English.
# ============================================================================
param(
  [string]$OracleHome = "D:\app\oracle\product\12.2.0\dbhome_1",
  [string]$Staging    = "D:\OraInstall\db12201"
)
try { chcp 65001 > $null } catch {}
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$pr=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){ Write-Host "[ABORT] Administrator rights required."; return }

function Has-VC2013 {
  if(Test-Path "$env:SystemRoot\System32\msvcr120.dll"){ return $true }
  $keys = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
  $n = Get-ItemProperty $keys -EA SilentlyContinue | Where-Object { $_.DisplayName -match 'Visual C\+\+ 2013.*(x64|Redistributable)' }
  return ($n -ne $null -and @($n).Count -gt 0)
}

Write-Host "===== [1/4] Detect Visual C++ 2013 x64 ====="
$before = Has-VC2013
Write-Host ("  System32\msvcr120.dll : " + $(if(Test-Path "$env:SystemRoot\System32\msvcr120.dll"){'present'}else{'MISSING'}))
Write-Host ("  VC++ 2013 x64 present : " + $(if($before){'yes'}else{'NO  <-- this breaks Oracle 12.2'}))
if($before){ Write-Host "  (already installed - will still verify sqlplus below)" }

if(-not $before){
  Write-Host "===== [2/4] Locate vcredist_x64.exe (offline, from the Oracle media) ====="
  $here = Split-Path -Parent $MyInvocation.MyCommand.Path
  $cands = @(
    (Join-Path $Staging 'database\stage\ext\vcredist_x64.exe'),
    (Join-Path $Staging 'database\stage\ext\bin\vcredist_x64.exe'),
    (Join-Path (Split-Path -Parent $here) 'installer\vcredist_x64.exe')
  )
  $vc = $cands | Where-Object { Test-Path $_ } | Select-Object -First 1
  if(-not $vc){
    $vc = Get-ChildItem $Staging -Recurse -Filter 'vcredist_x64.exe' -EA SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
  }
  if(-not $vc){
    Write-Host "  [ABORT] vcredist_x64.exe not found." -ForegroundColor Red
    Write-Host "          Extract the installer first (00 does this), or copy vcredist_x64.exe into the package 'installer' folder."
    return
  }
  Write-Host ("  using: " + $vc + " (" + [math]::Round((Get-Item $vc).Length/1MB,2) + " MB)")

  Write-Host "===== [3/4] Install silently ====="
  $p = Start-Process -FilePath $vc -ArgumentList '/install','/quiet','/norestart' -Wait -PassThru
  Write-Host ("  exit code = " + $p.ExitCode + "  (0 = installed, 1638/3010 = already present / reboot pending)")
  if($p.ExitCode -notin 0,1638,3010){
    Write-Host "  [WARN] unexpected exit code - trying legacy switches..."
    $p2 = Start-Process -FilePath $vc -ArgumentList '/q','/norestart' -Wait -PassThru
    Write-Host ("  exit code = " + $p2.ExitCode)
  }
  Write-Host ("  VC++ 2013 x64 now present : " + $(if(Has-VC2013){'yes'}else{'still NO'}))
} else {
  Write-Host "===== [2-3/4] (skipped install - already present) ====="
}

Write-Host "===== [4/4] Re-test sqlplus ====="
$sp = Join-Path $OracleHome 'bin\sqlplus.exe'
if(-not (Test-Path $sp)){ Write-Host "  sqlplus.exe not found at $sp - install Oracle (00) now that the runtime is present."; return }
$env:ORACLE_HOME=$OracleHome; $env:PATH="$OracleHome\bin;$env:PATH"
$v = (& $sp -V 2>&1 | Out-String).Trim()
if($v -match 'Release'){
  Write-Host ("[SUCCESS] " + (($v -split "`n" | Where-Object {$_ -match 'Release'} | Select-Object -First 1).Trim()))
  Write-Host "  The missing VC++ 2013 runtime was the cause - no reinstall needed."
  Write-Host "  NEXT: powershell -ExecutionPolicy Bypass -File scripts\01_install_and_createdb_ELEVATED.ps1"
} else {
  Write-Host "[STILL FAILING] sqlplus -V:"
  Write-Host ("  " + ($v -replace "`r?`n"," "))
  Write-Host "  The runtime is now in place, but this home was built without it and may be incomplete."
  Write-Host "  NEXT: powershell -ExecutionPolicy Bypass -File scripts\99_fix_reinstall.ps1   (clean reinstall, now with the runtime present)"
}
