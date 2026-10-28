# ============================================================================
#  96_start_db_listener.ps1   *** RUN AS ADMINISTRATOR ***
#  Fix for SQL Developer / JDBC error:
#     "The Network Adapter could not establish the connection"  (ORA-17002 / ORA-12541)
#  Meaning: nothing is listening at the host:port you connected to. Usually the
#  NAIMS12 instance and/or its listener (port 1522) are just STOPPED - NOT a
#  reason to reinstall. This starts them and verifies. Output is ASCII/English.
#     powershell -ExecutionPolicy Bypass -File scripts\96_start_db_listener.ps1
# ============================================================================
try { chcp 65001 > $null } catch {}
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

. "$PSScriptRoot\_secrets.ps1"   # 비밀번호는 코드에 두지 않는다 (환경변수 → 파라미터 → 화면 입력)
$OH="D:\app\oracle\product\12.2.0\dbhome_1"; $SID="NAIMS12"
$PW=Get-NaimsSecret -EnvName 'NAIMS12_SYS_PW' -Prompt 'NAIMS12 SYSTEM 비밀번호' -DefaultValue 'Naims12#2026'; $PORT=1522
$pr=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){ Write-Host "[ABORT] Administrator rights required (to start services)."; return }
if(-not (Test-Path "$OH\bin\sqlplus.exe")){ Write-Host "[ABORT] Oracle home not usable. Run 95/00 first."; return }

Write-Host "===== [1/3] Start database service OracleService$SID ====="
$svc = Get-Service "OracleService$SID" -EA SilentlyContinue
if(-not $svc){ Write-Host "  [WARN] service OracleService$SID does not exist -> NAIMS12 was never created. Run 01." }
else { $r = (net start "OracleService$SID" 2>&1 | Out-String); Write-Host ("  " + ($r -replace "`r?`n"," ").Trim()) }

Write-Host "===== [2/3] Start the listener (port $PORT) ====="
# try the listener Windows service first, then lsnrctl
$lsvc = Get-Service -Name "OracleOraDB12Home1TNSListener*" -EA SilentlyContinue | Select-Object -First 1
if($lsvc){ $r = (net start $lsvc.Name 2>&1 | Out-String); Write-Host ("  service: " + ($r -replace "`r?`n"," ").Trim()) }
& "$OH\bin\lsnrctl.exe" start LISTENER_N12 2>&1 | Out-Null
Start-Sleep 3

Write-Host "===== [3/3] Verify ====="
$portOk = (Test-NetConnection -ComputerName localhost -Port $PORT -WarningAction SilentlyContinue).TcpTestSucceeded
Write-Host ("  port $PORT : " + $(if($portOk){'LISTENING'}else{'still down'}))
$open = (@"
set head off feed off pages 0
select open_mode from v`$database;
exit
"@ | & "$OH\bin\sqlplus.exe" -S -L "system/$PW@localhost:$PORT/$SID" 2>&1 | Out-String)
if($open -match 'READ WRITE'){
  Write-Host "  NAIMS12 : OPEN (READ WRITE)"
  Write-Host "[SUCCESS] DB + listener are up. SQL Developer can connect now:"
  Write-Host "          host=localhost  port=$PORT  Service name=$SID (or LegacyNaims)  user=system"
} else {
  Write-Host ("  NAIMS12 : NOT reachable  (" + (($open -split "`n" | Where-Object {$_ -match 'ORA-'}) -join '; ').Trim() + ")")
  Write-Host "[CHECK] If port is still down: listener config missing -> re-run 01 step [8/8], or run 98_diagnose.ps1."
  Write-Host "        If port LISTENING but ORA-12514: service not registered yet - wait 30s and retry, or check service name."
  Write-Host "        Make sure SQL Developer uses PORT $PORT (not 1521) and Service name $SID."
}
