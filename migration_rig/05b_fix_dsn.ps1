# ============================================================================
#  05b_fix_dsn.ps1   -  Diagnose & (re)register the ODBC DSN the migrator uses.
#  마이그레이터 기본 소스 = DSN=LegacyNaims. 이 DSN 이 없거나 엉뚱한 드라이버를 가리키면 접속 실패.
#  하는 일: Oracle 12c ODBC 드라이버 탐지 → User DSN 'LegacyNaims' + (관리자면) System DSN 'naims' 등록.
#           과거 문제(HKCU User DSN 이 System DSN 을 가려 21c 드라이버로 붙던 것)도 정리.
#  Output is ASCII/English.  (User DSN 등록은 비관리자도 됨)
#    powershell -ExecutionPolicy Bypass -File scripts\05b_fix_dsn.ps1
# ============================================================================
try { chcp 65001 > $null } catch {}
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$DBQ = "localhost:1522/NAIMS12"
$driverName = "Oracle in OraDB12Home1"

Write-Host "===== 05b: ODBC DSN fix (LegacyNaims -> $DBQ) ====="

# 1) find the Oracle 12c ODBC driver
$inst = Get-ItemProperty "HKLM:\SOFTWARE\ODBC\ODBCINST.INI\$driverName" -EA SilentlyContinue
if(-not $inst -or -not (Test-Path $inst.Driver)){
  Write-Host "[FAIL] Oracle ODBC driver '$driverName' not found (or its DLL missing)."
  Write-Host "       Cause: Oracle 12c not installed, or a different home name."
  Write-Host "       List installed ODBC drivers:"
  Get-ChildItem "HKLM:\SOFTWARE\ODBC\ODBCINST.INI" -EA SilentlyContinue | ForEach-Object { Write-Host ("         - " + $_.PSChildName) }
  Write-Host "       Fix: run 00 (install) / 01 (which also registers driver), then re-run this."
  return
}
$drv = $inst.Driver
Write-Host "Driver OK: $drv"

function Set-Dsn($hive, $name){
  $base = "$hive`:\SOFTWARE\ODBC\ODBC.INI"
  New-Item -Path "$base\$name" -Force | Out-Null
  Set-ItemProperty "$base\$name" -Name 'Driver' -Value $drv
  Set-ItemProperty "$base\$name" -Name 'DBQ'    -Value $DBQ
  Set-ItemProperty "$base\$name" -Name 'Description' -Value 'NAIMS12 legacy source'
  New-Item -Path "$base\ODBC Data Sources" -Force | Out-Null
  Set-ItemProperty "$base\ODBC Data Sources" -Name $name -Value $driverName
}

# 2) User DSN 'LegacyNaims' (migrator default) - no admin needed
Set-Dsn 'HKCU' 'LegacyNaims'
Write-Host "Registered USER DSN 'LegacyNaims' -> $DBQ"

# 3) clean stale HKCU 'naims' that could shadow a System DSN with a wrong driver
$stale = "HKCU:\SOFTWARE\ODBC\ODBC.INI\naims"
if(Test-Path $stale){
  $d = (Get-ItemProperty $stale -EA SilentlyContinue).Driver
  if($d -and $d -ne $drv){ Remove-Item $stale -Recurse -Force -EA SilentlyContinue; Write-Host "Removed stale USER DSN 'naims' (wrong driver: $d)" }
}

# 4) System DSN 'naims' + 'LegacyNaims' if elevated
$pr = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if($pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
  Set-Dsn 'HKLM' 'naims'
  Set-Dsn 'HKLM' 'LegacyNaims'
  Write-Host "Registered SYSTEM DSN 'naims' and 'LegacyNaims' -> $DBQ"
} else {
  Write-Host "(not elevated: only USER DSN set. That's enough for the migrator's DSN=LegacyNaims.)"
}

# 5) show what's registered
Write-Host "----- current DSNs -----"
foreach($h in 'HKCU','HKLM'){
  $p = "$h`:\SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources"
  if(Test-Path $p){ (Get-Item $p).Property | Where-Object {$_ -in 'naims','LegacyNaims'} | ForEach-Object { Write-Host ("  $h  $_") } }
}
Write-Host "[DONE] Now test in the migrator: source = DSN=LegacyNaims;Uid=LEGACYNAIMS;Pwd=<입력>;  -> [Connection test]"
