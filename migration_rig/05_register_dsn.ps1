# 05_register_dsn.ps1  -  register User DSN 'LegacyNaims' (no admin needed, HKCU)
# Migrator default source = DSN=LegacyNaims;Uid=LEGACYNAIMS;Pwd=<시작 화면에서 입력>
# Output is ASCII/English.  If it fails, use 05b_fix_dsn.ps1 (more robust).
try { chcp 65001 > $null } catch {}
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$drvName = 'Oracle in OraDB12Home1'
$drv = (Get-ItemProperty "HKLM:\SOFTWARE\ODBC\ODBCINST.INI\$drvName" -EA SilentlyContinue).Driver
if (-not $drv) {
    Write-Host "[ABORT] ODBC driver '$drvName' not registered - check the 12c install (run 00/01)." -ForegroundColor Red
    Write-Host "        Or run 05b_fix_dsn.ps1 which lists available drivers and fixes the DSN."
    exit 1
}
New-Item -Path 'HKCU:\SOFTWARE\ODBC\ODBC.INI\LegacyNaims' -Force | Out-Null
Set-ItemProperty 'HKCU:\SOFTWARE\ODBC\ODBC.INI\LegacyNaims' -Name 'Driver'      -Value $drv
Set-ItemProperty 'HKCU:\SOFTWARE\ODBC\ODBC.INI\LegacyNaims' -Name 'DBQ'         -Value 'localhost:1522/LegacyNaims'
Set-ItemProperty 'HKCU:\SOFTWARE\ODBC\ODBC.INI\LegacyNaims' -Name 'Description' -Value 'Legacy NAIMS source (12c)'
New-Item -Path 'HKCU:\SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources' -Force | Out-Null
Set-ItemProperty 'HKCU:\SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources' -Name 'LegacyNaims' -Value $drvName
Write-Host "User DSN 'LegacyNaims' registered -> localhost:1522/LegacyNaims ($drvName)"
Write-Host "Test in the migrator: source = DSN=LegacyNaims;Uid=LEGACYNAIMS;Pwd=<입력>;  -> [Connection test]"
