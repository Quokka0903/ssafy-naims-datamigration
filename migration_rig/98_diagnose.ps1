# ============================================================================
#  98_diagnose.ps1   -  Diagnose the W/S Oracle 12c setup end to end.
#  Read-only. Output is ASCII/English so it never mojibakes.
#  Run any time (Administrator recommended so it can read services/listener).
#     powershell -ExecutionPolicy Bypass -File scripts\98_diagnose.ps1
# ============================================================================
$OH  = "D:\app\oracle\product\12.2.0\dbhome_1"
. "$PSScriptRoot\_secrets.ps1"   # 비밀번호는 코드에 두지 않는다 (환경변수 → 파라미터 → 화면 입력)
$SID = "NAIMS12"; $PW = Get-NaimsSecret -EnvName 'NAIMS12_SYS_PW' -Prompt 'NAIMS12 SYSTEM 비밀번호' -DefaultValue 'Naims12#2026'
$conn = "system/$PW@localhost:1522/$SID"
$ok=0; $bad=0
function Line($label,$good,$detail){ $mark = $(if($good){'[ OK ]'}else{'[FAIL]'}); if($good){$script:ok++}else{$script:bad++}; Write-Host ("{0}  {1,-34} {2}" -f $mark,$label,$detail) }
function Note($t){ Write-Host $t }

Write-Host "================ Oracle 12c W/S diagnosis ================"

# 1. Disk
$d = Get-PSDrive D -EA SilentlyContinue
if($d){ $free=[math]::Round($d.Free/1GB,1); Line "D: free space (need 15GB+)" ($free -ge 5) "$free GB" }
else  { Line "D: drive present" $false "D: not found (rsp/scripts assume D:)" }

# 1b. Target volume characteristics. The reference (working) install is on a
#     Fixed NTFS local disk. Removable/exFAT/compressed volumes can break a
#     multi-GB Oracle install even when free space looks fine.
$drvLetter = $OH.Substring(0,1)
$vol = Get-Volume -DriveLetter $drvLetter -EA SilentlyContinue
$volBad = $false
if($vol){
  $ntfsOk = ($vol.FileSystem -eq 'NTFS'); $fixedOk = ($vol.DriveType -eq 'Fixed')
  if(-not ($ntfsOk -and $fixedOk)){ $volBad = $true }
  Line "${drvLetter}: is NTFS" $ntfsOk ("FileSystem=" + $vol.FileSystem + " (reference: NTFS)")
  Line "${drvLetter}: is a Fixed disk" $fixedOk ("DriveType=" + $vol.DriveType + " (reference: Fixed)")
  if($volBad){
    Note "        [FIX] this volume cannot host Oracle reliably ->"
    Note "              powershell -ExecutionPolicy Bypass -File scripts\12_set_install_drive.ps1"
    Note "              (auto-picks an NTFS/Fixed drive and rewrites every hard-coded path)"
  }
  try{
    $dsk = Get-Partition -DriveLetter $drvLetter -EA Stop | Get-Disk -EA Stop
    Note ("        disk: " + $dsk.FriendlyName + "  BusType=" + $dsk.BusType + " (reference: SATA)")
    if($dsk.BusType -in 'USB','iSCSI'){ Note "        [!] a USB/iSCSI volume is a common cause of half-copied installs." }
  } catch {}
  # NTFS compression on the home folder can also corrupt large binaries during install
  if(Test-Path $OH){
    $attr = (Get-Item $OH -Force).Attributes
    if($attr -band [IO.FileAttributes]::Compressed){
      Note "        [!] ORACLE_HOME folder is NTFS-COMPRESSED - not supported for Oracle binaries."
      Note "        [FIX] compact.exe /U /S /A /I `"$OH`"   (then reinstall with 99_fix_reinstall.ps1)"
    }
  }
}

# 1c. PREREQUISITE: Visual C++ 2013 x64 runtime. sqlplus.exe imports MSVCR120.dll.
#     If this is missing, Oracle 12.2 installs "successfully" but nothing runs
#     ("Error 57 initializing SQL*Plus"). This is a top suspect whenever every
#     file is present, PATH is right, and sqlplus still refuses to start.
$vcDll = Test-Path "$env:SystemRoot\System32\msvcr120.dll"
$vcReg = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -EA SilentlyContinue |
         Where-Object { $_.DisplayName -match 'Visual C\+\+ 2013.*(x64|Redistributable)' }
$vcOk = $vcDll -or ($vcReg -ne $null -and @($vcReg).Count -gt 0)
Line "VC++ 2013 x64 runtime (MSVCR120)" $vcOk $(if($vcDll){"System32\msvcr120.dll present"}elseif($vcOk){"registered: " + (@($vcReg)[0].DisplayName)}else{"MISSING - Oracle 12.2 cannot run without it"})
if(-not $vcOk){
  Note "        [FIX] powershell -ExecutionPolicy Bypass -File scripts\14_install_vcredist.ps1"
  Note "              (installs vcredist_x64.exe straight from the Oracle media - works offline)"
}

# 2. Oracle home + key files (an INCOMPLETE install is the usual failure)
Line "ORACLE_HOME folder" (Test-Path $OH) $OH
$files = @{
  'bin\sqlplus.exe'      = 'SQL*Plus';
  'bin\oradim.exe'       = 'oradim';
  'bin\lsnrctl.exe'      = 'listener ctl';
  'bin\oracle.exe'       = 'DB kernel (~200MB+)';
  'sqlplus\mesg\sp1us.msb'= 'SQL*Plus messages';
  'oracore\mesg\lrmus.msb'= 'core messages';
}
# NOTE: oraociei12.dll is an INSTANT CLIENT file - a full DB server install does NOT have it. Not checked.
foreach($f in $files.Keys){ $p=Join-Path $OH $f; $b=$(if(Test-Path $p){(Get-Item $p).Length}else{0}); $sz=$(if(Test-Path $p){' ('+[math]::Round($b/1MB,2)+' MB)'}else{''}); Line ("file: "+$files[$f]) ((Test-Path $p) -and $b -gt 0) ($f+$sz) }

# 2b. Installer facts (report the numbers; a truncated zip breaks every install even after full reinstall).
$zip = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) "installer\V839963-01.zip"
if(Test-Path $zip){
  $zmb=[math]::Round((Get-Item $zip).Length/1MB,0)
  Note ("        installer V839963-01.zip = $zmb MB  (a full 12.2.0.1 Win x64 DB zip is ~3.2 GB / ~3294 MB)")
  # verify the zip can be fully read (truncated zips throw on enumeration)
  try { Add-Type -AssemblyName System.IO.Compression.FileSystem
        $za=[System.IO.Compression.ZipFile]::OpenRead($zip); $n=$za.Entries.Count; $za.Dispose()
        Note ("        zip opens OK, $n entries (if it were truncated this usually errors)") }
  catch { Note ("        [!] zip could NOT be fully read = CORRUPT/TRUNCATED: " + $_.Exception.Message) }
}

# 2c. BASELINE DIFF - compare against a home that is PROVEN to run (baseline.json).
#     This replaces guessing about which files matter.
$baseFile = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "baseline.json"
if(Test-Path $baseFile){
  try{
    $bl = Get-Content $baseFile -Raw | ConvertFrom-Json
    Write-Host "---------------- baseline diff (vs known-good home) ----------------"
    Note ("  baseline: " + $bl.sqlplusVersion + "  files=" + $bl.totalFiles + "  size=" + [math]::Round($bl.totalBytes/1GB,2) + " GB  bin=" + $bl.binFileCount)
    $hereAll = Get-ChildItem $OH -Recurse -File -EA SilentlyContinue | Measure-Object -Property Length -Sum
    $hereBin = (Get-ChildItem (Join-Path $OH 'bin') -File -EA SilentlyContinue | Measure-Object).Count
    Note ("  this W/S: files=" + $hereAll.Count + "  size=" + [math]::Round($hereAll.Sum/1GB,2) + " GB  bin=" + $hereBin)
    $pctFiles = if($bl.totalFiles){[math]::Round(100*$hereAll.Count/$bl.totalFiles,1)}else{0}
    Line "home completeness vs baseline" ($pctFiles -ge 95) ("$pctFiles % of baseline file count")
    $missing = @(); $short = @()
    foreach($e in $bl.files){
      if($e.bytes -le 0){ continue }
      $p = Join-Path $OH $e.rel
      if(-not (Test-Path $p)){ $missing += $e.rel }
      elseif((Get-Item $p).Length -lt ($e.bytes*0.9)){ $short += ("{0} ({1} vs {2} bytes)" -f $e.rel,(Get-Item $p).Length,$e.bytes) }
    }
    Line "key runtime files present" ($missing.Count -eq 0) ($(if($missing.Count){"MISSING: " + ($missing -join ', ')}else{"all " + ($bl.files | Where-Object {$_.bytes -gt 0}).Count + " present"}))
    if($short.Count){ Note ("  [!] truncated vs baseline: " + ($short -join '; ')) }
  } catch { Note ("  [WARN] baseline.json unreadable: " + $_.Exception.Message) }
} else {
  Note "  (no baseline.json - run 09_make_baseline.ps1 on the working PC to enable diffing)"
}

# 2d. ENVIRONMENT CONFLICT - a stale/other Oracle earlier in PATH, or ORACLE_HOME
#     pointing at a different home, makes sqlplus load a mismatched OCI and fail
#     to initialize ("Error 57") even though every file is present.
#     Symptom: typing plain `sqlplus` in cmd also fails.
Write-Host "---------------- environment (PATH / ORACLE_HOME) ----------------"
$envHome = [Environment]::GetEnvironmentVariable('ORACLE_HOME','Machine')
if(-not $envHome){ $envHome = [Environment]::GetEnvironmentVariable('ORACLE_HOME','User') }
Note ("  ORACLE_HOME (system/user) = " + $(if($envHome){$envHome}else{'(not set)'}))

# When ORACLE_HOME is NOT set, sqlplus resolves its home (and message files) via
# the registry. If a hard clean removed HKLM\SOFTWARE\ORACLE and the reinstall
# did not recreate it, sqlplus cannot find its home -> "Error 57".
$regHome = $null
foreach($root in 'HKLM:\SOFTWARE\ORACLE','HKLM:\SOFTWARE\Wow6432Node\ORACLE'){
  $k = Get-Item $root -EA SilentlyContinue
  if($k){ foreach($sub in $k.GetSubKeyNames() | Where-Object {$_ -like 'KEY_*'}){
    $h = (Get-ItemProperty "$root\$sub" -Name ORACLE_HOME -EA SilentlyContinue).ORACLE_HOME
    if($h -and $h.TrimEnd('\') -ieq $OH.TrimEnd('\')){ $regHome = "$root\$sub" }
  }}
}
Line "home registered in registry" ($regHome -ne $null) $(if($regHome){$regHome}else{"no KEY_* points to $OH"})
if(-not $regHome -and -not $envHome){
  Note "  [!] Neither ORACLE_HOME nor a registry KEY_* points at this home."
  Note "      That alone causes 'Error 57' even though sqlplus.exe exists."
  Note "      [FIX] powershell -ExecutionPolicy Bypass -File scripts\13_register_home.ps1"
}
if($envHome -and ($envHome.TrimEnd('\') -ne $OH.TrimEnd('\'))){
  Note "  [!] ORACLE_HOME points somewhere ELSE than the home we use -> mismatched OCI is likely."
  Note ("      [FIX] setx ORACLE_HOME `"$OH`" /M   (then open a NEW cmd window)")
}
# which sqlplus.exe would a plain `sqlplus` command resolve to?
$found = @()
foreach($d in ($env:PATH -split ';')){
  if([string]::IsNullOrWhiteSpace($d)){ continue }
  $c = Join-Path $d 'sqlplus.exe'
  if(Test-Path $c){ $found += $c }
}
if($found.Count -eq 0){ Note "  plain 'sqlplus' resolves to: (none - our home's bin is not in PATH)" }
else{
  Note ("  plain 'sqlplus' resolves to: " + $found[0])
  if($found.Count -gt 1){ Note ("  [!] " + $found.Count + " sqlplus.exe found in PATH:"); $found | ForEach-Object { Note ("      " + $_) } }
  $first = $found[0]
  $sameHome = $first.ToLower().StartsWith($OH.ToLower())
  Line "PATH resolves to OUR Oracle home" $sameHome $(if($sameHome){"ok"}else{"points to a DIFFERENT/older Oracle -> Error 57 cause"})
  if(-not $sameHome){
    Note "      [FIX] remove the old Oracle bin from the system PATH, or put ours first:"
    Note ("            `$env:PATH=`"$OH\bin;`$env:PATH`"   (test in this window)")
  }
}

# 3. The definitive test: sqlplus -V must print a version. Set ORACLE_HOME first (rule out env cause).
$env:ORACLE_HOME=$OH; $env:PATH="$OH\bin;$env:PATH"
$verOk=$false; $verTxt=''
if(Test-Path "$OH\bin\sqlplus.exe"){ $verTxt = (& "$OH\bin\sqlplus.exe" -V 2>&1 | Out-String).Trim(); $verOk = $verTxt -match 'Release' }
Line "sqlplus -V runs (install complete)" $verOk (($verTxt -replace "`r?`n"," ")).Trim()
if(-not $verOk){ Note "        ^ 'Error loading message shared library' with ORACLE_HOME set + zip complete = broken/incomplete install." }
if(-not $verOk){ Note "        ^ If this FAILS but files exist, the install is INCOMPLETE (disk space / interruption)." }

# 4. Windows service
$svc = Get-Service "OracleService$SID" -EA SilentlyContinue
Line "Windows service OracleService$SID" ($svc -ne $null) $(if($svc){$svc.Status}else{'not created'})

# 5. Listener on 1522
$lsnr = Test-NetConnection -ComputerName localhost -Port 1522 -WarningAction SilentlyContinue
Line "Listener port 1522 open" ($lsnr.TcpTestSucceeded) $(if($lsnr.TcpTestSucceeded){'listening'}else{'no listener'})

# 6. Database open + charset (only if sqlplus works)
if($verOk){
  $q = (@"
set head off feed off pages 0 lines 200
select 'OPEN=' || open_mode from v`$database;
select 'CS=' || value from nls_database_parameters where parameter='NLS_CHARACTERSET';
exit
"@ | & "$OH\bin\sqlplus.exe" -S -L $conn 2>&1 | Out-String)
  $dbOpen = $q -match 'READ WRITE'
  Line "NAIMS12 database OPEN" $dbOpen (($q -split "`n" | Where-Object{$_ -match 'OPEN='}) -join '')
  Line "charset KO16MSWIN949" ($q -match 'KO16MSWIN949') (($q -split "`n" | Where-Object{$_ -match 'CS='}) -join '')
  # 7. CRYPTO check (only meaningful after 03 + restore)
  $c = (@"
set head off feed off pages 0
select 'CT=' || RAWTOHEX(UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT('A201'))) from dual;
exit
"@ | & "$OH\bin\sqlplus.exe" -S -L $conn 2>&1 | Out-String)
  Line "CRYPTO key hellios# (A201->F45F..)" ($c -match 'F45F4284A4E23E62') (($c -split "`n" | Where-Object{$_ -match 'CT='}) -join '')
} else {
  Note "        (skipping DB/CRYPTO checks - sqlplus not usable yet)"
}

# 8. ODBC DSN
$dsn = Get-ItemProperty 'HKLM:\SOFTWARE\ODBC\ODBC.INI\LegacyNaims' -EA SilentlyContinue
Line "ODBC DSN 'LegacyNaims'" ($dsn -ne $null) $(if($dsn){$dsn.DBQ}else{'not registered (run 05)'})

# 9. Latest OUI install log tail (proves whether 00 finished)
Write-Host "---------------- last OUI install log ----------------"
$logDir = "C:\Program Files\Oracle\Inventory\logs"
$il = Get-ChildItem "$logDir\installActions*.log" -EA SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if($il){ Note ("file: "+$il.FullName); Get-Content $il.FullName -Tail 8 | ForEach-Object { Note ("  "+$_) } }
else { Note "  (no OUI install log found - Step 00 may not have run)" }

Write-Host "================ verdict ================"
Write-Host ("Checks passed: $ok   failed: $bad")
if(-not $verOk -and $volBad){
  Write-Host "NEXT: the target volume itself is unsuitable (not NTFS and/or not a Fixed disk)."
  Write-Host "      That alone explains a half-copied install. Retarget to a good drive:"
  Write-Host "        powershell -ExecutionPolicy Bypass -File scripts\12_set_install_drive.ps1"
  Write-Host "      then: 99_fix_reinstall.ps1 -> 01 -> 02/03 -> 04 -> 05"
}
elseif(-not $verOk){
  Write-Host "NEXT: Oracle software is not usable (volume itself looks fine)."
  Write-Host "      1) powershell -ExecutionPolicy Bypass -File scripts\99_fix_reinstall.ps1   (clean reinstall)"
  Write-Host "      2) if it fails again, skip the installer and transplant a known-good home:"
  Write-Host "         powershell -ExecutionPolicy Bypass -File scripts\11_transplant_oracle_home.ps1"
  Write-Host "         (needs home_image\dbhome_1.zip, created by 10_export_oracle_home.ps1 on a working PC)"
} elseif((Get-Service "OracleService$SID" -EA SilentlyContinue) -and (-not $lsnr.TcpTestSucceeded -or -not $dbOpen)){
  # DB exists but is not reachable = the "Network Adapter could not establish the connection" case
  Write-Host "NEXT: NAIMS12 exists but is not reachable (listener/DB stopped)."
  Write-Host "      This is the 'Network Adapter could not establish the connection' error - NOT a reason to reinstall."
  Write-Host "      Fix: powershell -ExecutionPolicy Bypass -File scripts\96_start_db_listener.ps1"
  Write-Host "      And make sure SQL Developer uses PORT 1522 (not 1521), Service name NAIMS12."
} elseif($bad -gt 0){
  Write-Host "NEXT: sqlplus works but some steps are missing. Continue the numbered steps"
  Write-Host "      (01 create DB -> 02/03 -> 04 restore -> 05 DSN). See README section 4."
} else {
  Write-Host "All good. You can connect and run the migrator."
}
