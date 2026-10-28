# ============================================================================
#  00_install_software_ELEVATED.ps1      *** RUN AS ADMINISTRATOR ***
#  Install Oracle 12c (12.2.0.1) SOFTWARE ONLY (INSTALL_DB_SWONLY).
#  Run this FIRST - it creates the sqlplus/oradim/lsnrctl that 01 (create DB) needs.
#  Output is ASCII/English so it never mojibakes on any console codepage.
#
#  Steps:
#   1) verify Administrator
#   2) unzip installer\V839963-01.zip to D:\OraInstall\db12201 (get setup.exe)
#   3) silent install using scripts\db_install_naims12.rsp
#   4) verify with 'sqlplus -V' (not just file existence) = complete install
#
#  Home: D:\app\oracle\product\12.2.0\dbhome_1  (must match rsp ORACLE_HOME)
#  If there is no D: drive: change ORACLE_HOME/ORACLE_BASE in db_install_naims12.rsp
#  and $OH/$extract here and $OH in 01 to the SAME existing path.
# ============================================================================
try { chcp 65001 > $null } catch {}
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
$ErrorActionPreference = 'Continue'

$pr = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "[ABORT] Run as Administrator (right-click PowerShell -> Run as administrator)." -ForegroundColor Red
    return
}

$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$pkgRoot = Split-Path -Parent $here
$zip     = Join-Path $pkgRoot "installer\V839963-01.zip"
$rsp     = Join-Path $here "db_install_naims12.rsp"
$extract = "D:\OraInstall\db12201"                       # ASCII path required
$setup   = Join-Path $extract "database\setup.exe"
$OH      = "D:\app\oracle\product\12.2.0\dbhome_1"        # must match rsp ORACLE_HOME

New-Item -ItemType Directory -Force -Path "D:\OraInstall" | Out-Null

if (-not (Test-Path $zip)) { Write-Host "[ABORT] installer not found: $zip" -ForegroundColor Red; return }
if (-not (Test-Path $rsp)) { Write-Host "[ABORT] response file not found: $rsp" -ForegroundColor Red; return }

# Disk space precheck (the #1 cause of the 'Error 57' failure is running out of space)
$dfree = (Get-PSDrive D -EA SilentlyContinue).Free
if ($dfree -ne $null) {
    $gb = [math]::Round($dfree/1GB,1)
    Write-Host "D: free = $gb GB (need 15GB+)"
    if ($gb -lt 15) { Write-Host "[WARN] Low free space on D:. Install may abort mid-way ('Error 57'). Free up space first." -ForegroundColor Yellow }
}

# Skip only if a COMPLETE install already exists (verify with sqlplus -V, not just file presence)
if (Test-Path (Join-Path $OH "bin\sqlplus.exe")) {
    $v = (& (Join-Path $OH "bin\sqlplus.exe") -V 2>&1 | Out-String)
    if ($v -match 'Release') {
        Write-Host "[SKIP] Already installed and working: $OH" -ForegroundColor Yellow
        Write-Host "       Proceed to 01_install_and_createdb_ELEVATED.ps1"
        return
    } else {
        Write-Host "[INCOMPLETE] sqlplus.exe exists but cannot run ('Error 57 / message shared library')." -ForegroundColor Red
        Write-Host "             Run 98_diagnose.ps1 FIRST - it checks whether the installer zip is TRUNCATED"
        Write-Host "             (the usual real cause: an incomplete V839963-01.zip). If the zip is complete,"
        Write-Host "             run 99_fix_reinstall.ps1 for a full clean reinstall."
        return
    }
}

# 2) unzip (only if setup.exe not present yet)
if (-not (Test-Path $setup)) {
    Write-Host "===== [1/3] Unzip installer -> $extract  (several minutes) $(Get-Date -f HH:mm:ss) ====="
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    try { Expand-Archive -Path $zip -DestinationPath $extract -Force }
    catch { Write-Host "[ABORT] unzip failed: $($_.Exception.Message)" -ForegroundColor Red; return }
}
if (-not (Test-Path $setup)) { Write-Host "[ABORT] setup.exe not found: $setup" -ForegroundColor Red; return }

# 2b) PREREQUISITE: Visual C++ 2013 x64 runtime (sqlplus.exe imports MSVCR120.dll).
#     The Oracle installer normally does this itself - the media ships
#     stage\ext\vcredist_x64.exe and OUI runs it (MSVCREDIST_INSTALL=true, verified
#     in installActions log). But on some machines that step does not take effect
#     (blocked/failed silently), and then Oracle installs "successfully" while
#     nothing runs -> "Error 57 initializing SQL*Plus".
#     So: install it explicitly here and verify, instead of trusting the implicit step.
$hasVC = (Test-Path "$env:SystemRoot\System32\msvcr120.dll")
if(-not $hasVC){
    $vcKeys = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -EA SilentlyContinue |
              Where-Object { $_.DisplayName -match 'Visual C\+\+ 2013.*(x64|Redistributable)' }
    $hasVC = ($vcKeys -ne $null -and @($vcKeys).Count -gt 0)
}
Write-Host ("===== [1b/3] Prerequisite: Visual C++ 2013 x64 = " + $(if($hasVC){'present'}else{'MISSING - installing'}) + " =====")
if(-not $hasVC){
    $vc = @((Join-Path $extract 'database\stage\ext\vcredist_x64.exe'),
            (Join-Path $extract 'database\stage\ext\bin\vcredist_x64.exe'),
            (Join-Path $pkgRoot 'installer\vcredist_x64.exe')) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if($vc){
        Write-Host ("  installing " + $vc)
        $vp = Start-Process -FilePath $vc -ArgumentList '/install','/quiet','/norestart' -Wait -PassThru
        Write-Host ("  vcredist exit code = " + $vp.ExitCode + " (0 / 1638 / 3010 = ok)")
    } else {
        Write-Host "  [WARN] vcredist_x64.exe not found in the media - Oracle will install but may not run." -ForegroundColor Yellow
        Write-Host "         (see scripts\14_install_vcredist.ps1)"
    }
}

# 3) silent install (software only)
Write-Host "===== [2/3] Oracle 12c silent software install starting $(Get-Date -f HH:mm:ss) ====="
Write-Host "        (may take 10-20 min. Wait until the window closes.)"
& $setup -silent -responseFile $rsp -waitforcompletion -ignorePrereqFailure -noconfig
Write-Host "        setup.exe exit code = $LASTEXITCODE  (0 or 6 = success family)"

# 4) verify with 'sqlplus -V' (catches incomplete installs where files exist but do not run)
Write-Host "===== [3/3] Verify install (sqlplus -V) ====="
$env:ORACLE_HOME=$OH; $env:PATH="$OH\bin;$env:PATH"
$sqlplus = Join-Path $OH "bin\sqlplus.exe"
$verOk = $false; $verTxt = ''
if (Test-Path $sqlplus) { $verTxt = (& $sqlplus -V 2>&1 | Out-String).Trim(); $verOk = $verTxt -match 'Release' }
if (-not $verOk -and (Test-Path $sqlplus)) {
    # SELF-HEAL: an unregistered home (no HKLM\SOFTWARE\ORACLE\KEY_*) causes "Error 57"
    # even though every file is present. Register and retry before reporting failure.
    Write-Host "  sqlplus -V failed - ensuring the home is registered..."
    $s13 = Join-Path $here "13_register_home.ps1"
    if (Test-Path $s13) { & powershell -ExecutionPolicy Bypass -File $s13 -OracleHome $OH | ForEach-Object { Write-Host ("    " + $_) } }
    $verTxt = (& $sqlplus -V 2>&1 | Out-String).Trim(); $verOk = $verTxt -match 'Release'
}
if ($verOk) {
    Write-Host ("[SUCCESS] install complete - " + (($verTxt -split "`n" | Where-Object {$_ -match 'Release'} | Select-Object -First 1).Trim())) -ForegroundColor Green
    Write-Host "Next: powershell -ExecutionPolicy Bypass -File scripts\01_install_and_createdb_ELEVATED.ps1"
} else {
    Write-Host "[FAIL] install did not complete (sqlplus -V failed: 'Error 57 / message shared library')." -ForegroundColor Red
    if ($verTxt) { Write-Host ("       " + ($verTxt -replace "`r?`n"," ")) }
    Write-Host "  Most likely: the installer zip (V839963-01.zip) is TRUNCATED - a full 12.2 Win x64 DB zip is ~3294 MB."
    Write-Host "  Action: 1) diagnose  powershell -ExecutionPolicy Bypass -File scripts\98_diagnose.ps1   (checks the zip size)"
    Write-Host "          2) if zip is short -> obtain a COMPLETE installer zip (this is not fixable by re-running)."
    Write-Host "          3) if zip is complete -> powershell -ExecutionPolicy Bypass -File scripts\99_fix_reinstall.ps1"
}
Write-Host "===== 00 end $(Get-Date -f HH:mm:ss) ====="
