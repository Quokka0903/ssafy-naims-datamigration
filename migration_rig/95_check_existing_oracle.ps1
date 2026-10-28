# ============================================================================
#  95_check_existing_oracle.ps1  -  Inspect ANY Oracle already installed on this W/S
#  and tell you what to do: proceed / adopt-existing-home / reset-reinstall.
#  Read-only (does not change anything). Output is ASCII/English.
#     powershell -ExecutionPolicy Bypass -File scripts\95_check_existing_oracle.ps1
#
#  Handles the field case where the W/S already has an Oracle 12c (or 11g/19c/21c)
#  from before. It finds every Oracle home, its version, whether it runs, existing
#  DBs/services/listeners, and prints a VERDICT with the exact next command.
# ============================================================================
try { chcp 65001 > $null } catch {}
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$ourHome = "D:\app\oracle\product\12.2.0\dbhome_1"
$ourBase = "D:\app\oracle"
. "$PSScriptRoot\_secrets.ps1"   # 비밀번호는 코드에 두지 않는다 (환경변수 → 파라미터 → 화면 입력)
$ourSid  = "NAIMS12"; $ourPort = 1522; $ourPw = Get-NaimsSecret -EnvName 'NAIMS12_SYS_PW' -Prompt 'NAIMS12 SYSTEM 비밀번호' -DefaultValue 'Naims12#2026'

function Ver($hp){
  $sp = Join-Path $hp "bin\sqlplus.exe"
  if(-not (Test-Path $sp)){ return @{ ok=$false; ver=""; major=0 } }
  $t = (& $sp -V 2>&1 | Out-String)
  if($t -match 'Release (\d+)\.(\d+)\.'){ return @{ ok=$true; ver=($t -replace "`r?`n"," ").Trim(); major=[int]$Matches[1] } }
  return @{ ok=$false; ver=($t -replace "`r?`n"," ").Trim(); major=0 }   # files exist but broken
}

Write-Host "================ Existing Oracle inspection ================"

# 1) collect homes from registry (KEY_*) and inventory
$homes = New-Object System.Collections.Generic.List[string]
foreach($root in 'HKLM:\SOFTWARE\ORACLE','HKLM:\SOFTWARE\Wow6432Node\ORACLE'){
  $k = Get-Item $root -EA SilentlyContinue
  if($k){ foreach($sub in $k.GetSubKeyNames() | Where-Object {$_ -like 'KEY_*'}){
    $h = (Get-ItemProperty "$root\$sub" -Name ORACLE_HOME -EA SilentlyContinue).ORACLE_HOME
    if($h -and -not $homes.Contains($h)){ $homes.Add($h) }
  }}
}
$instLoc = (Get-ItemProperty 'HKLM:\SOFTWARE\Oracle' -Name inst_loc -EA SilentlyContinue).inst_loc
if(-not $instLoc){ $instLoc = "C:\Program Files\Oracle\Inventory" }
$invXml = Join-Path $instLoc "ContentsXML\inventory.xml"
if(Test-Path $invXml){
  Select-String -Path $invXml -Pattern 'LOC="([^"]+)"' -AllMatches | ForEach-Object { $_.Matches } | ForEach-Object {
    $h = $_.Groups[1].Value; if($h -and -not $homes.Contains($h)){ $homes.Add($h) }
  }
}

if($homes.Count -eq 0){ Write-Host "No Oracle home found (registry + inventory empty)." }
else {
  Write-Host "Oracle homes found:"
  foreach($h in $homes){
    $v = Ver $h
    $tag = if(-not (Test-Path (Join-Path $h 'bin\sqlplus.exe'))){'(folder gone - stale inventory entry)'}
           elseif($v.ok){"Release major $($v.major) - $($v.ver)"} else {'(sqlplus present but NOT runnable = incomplete)'}
    $mark = if($h -ieq $ourHome){'[OUR HOME] '}else{''}
    Write-Host ("  - $mark$h  =>  $tag")
  }
}

# 2) services + listeners
Write-Host "----- services -----"
$svcs = Get-Service Oracle* -EA SilentlyContinue
if($svcs){ $svcs | ForEach-Object { Write-Host ("  {0}  {1}" -f $_.Status,$_.Name) } } else { Write-Host "  (no Oracle services)" }
Write-Host "----- listeners (ports) -----"
foreach($p in 1521,1522){
  $t = Test-NetConnection -ComputerName localhost -Port $p -WarningAction SilentlyContinue
  Write-Host ("  port $p : " + $(if($t.TcpTestSucceeded){'LISTENING'}else{'-'}))
}

# 3) our target checks
$ourVer = Ver $ourHome
$naimsOpen = $false
if($ourVer.ok){
  $q = (@"
set head off feed off pages 0
select open_mode from v`$database;
exit
"@ | & (Join-Path $ourHome 'bin\sqlplus.exe') -S -L "system/$ourPw@localhost:$ourPort/$ourSid" 2>&1 | Out-String)
  $naimsOpen = $q -match 'READ WRITE'
}

# 4) find a usable 12c/18c/19c home that is NOT our home (adopt candidate). 21c excluded (no OBFUSCATION_TOOLKIT).
$adopt = $null; $has21 = $false
foreach($h in $homes){
  if($h -ieq $ourHome){ continue }
  $v = Ver $h
  if($v.ok){
    if($v.major -ge 21){ $has21 = $true }
    elseif($v.major -ge 12 -and -not $adopt){ $adopt = @{ home=$h; ver=$v } }
  }
}

Write-Host "================ VERDICT ================"
if($ourVer.ok -and $naimsOpen){
  Write-Host "READY: our home works and NAIMS12 is OPEN."
  Write-Host "  NEXT: scripts\99_verify.ps1  (or run the migrator: DSN=LegacyNaims)"
}
elseif($ourVer.ok -and (Get-Service "OracleService$ourSid" -EA SilentlyContinue)){
  Write-Host "Our 12c home works and NAIMS12 service EXISTS but the DB is not open (stopped)."
  Write-Host "  NEXT (just start it, do NOT recreate):"
  Write-Host "    net start OracleService$ourSid"
  Write-Host "    `"$ourHome\bin\lsnrctl.exe`" start LISTENER_N12"
  Write-Host "    then: scripts\99_verify.ps1"
  Write-Host "  (Run 01 ONLY to rebuild from scratch - it DROPS and recreates NAIMS12.)"
}
elseif($ourVer.ok){
  Write-Host "Our 12c home works but NAIMS12 does not exist yet."
  Write-Host "  NEXT: powershell -ExecutionPolicy Bypass -File scripts\01_install_and_createdb_ELEVATED.ps1"
}
elseif((Test-Path (Join-Path $ourHome 'bin\sqlplus.exe')) -and -not $ourVer.ok){
  Write-Host "Our home exists but is INCOMPLETE (sqlplus not runnable)."
  Write-Host "  NEXT: powershell -ExecutionPolicy Bypass -File scripts\99_fix_reinstall.ps1"
}
elseif($adopt){
  Write-Host ("Existing usable Oracle {0} found at a DIFFERENT home:" -f $adopt.ver.major)
  Write-Host ("    $($adopt.home)")
  Write-Host "  OPTION A (adopt it, apply OUR settings = create NAIMS12 in that home):"
  Write-Host ("    powershell -ExecutionPolicy Bypass -File scripts\01_install_and_createdb_ELEVATED.ps1 -OracleHome `"$($adopt.home)`" -OracleBase `"$([System.IO.Path]::GetFullPath((Join-Path $adopt.home '..\..\..')))`"")
  Write-Host "    (then 02/03 crypto, 04 restore, 05 DSN as usual)"
  Write-Host "  OPTION B (ignore it and install our own clean 12c):"
  Write-Host "    powershell -ExecutionPolicy Bypass -File scripts\00_install_software_ELEVATED.ps1   (coexists)"
  if($has21){ Write-Host "  NOTE: a 21c home also exists - do NOT adopt 21c (DBMS_OBFUSCATION_TOOLKIT removed -> crypto fails)." }
}
elseif($has21){
  Write-Host "Only a 21c Oracle is present. 21c REMOVED DBMS_OBFUSCATION_TOOLKIT -> our CRYPTO cannot run there."
  Write-Host "  NEXT: install our 12c with scripts\00 (it can coexist), then 01..05."
  Write-Host "        (Alternatively keep names encrypted and decrypt in C# - advanced.)"
}
elseif($homes.Count -gt 0){
  Write-Host "Oracle homes are registered but none are runnable (stale/broken)."
  Write-Host "  NEXT: scripts\99_fix_reinstall.ps1  (cleans inventory+registry, then reinstalls)"
}
else{
  Write-Host "No Oracle installed."
  Write-Host "  NEXT: powershell -ExecutionPolicy Bypass -File scripts\00_install_software_ELEVATED.ps1"
}
Write-Host "========================================="
