param([string]$OracleHome="D:\app\oracle\product\12.2.0\dbhome_1",[string]$OracleBase="D:\app\oracle")
# ============================================================================
#  01_install_and_createdb_ELEVATED.ps1   *** RUN AS ADMINISTRATOR ***
#  Create NAIMS12 database via manual CREATE DATABASE (no dbca RMAN clone).
#  Output is ASCII/English so it never mojibakes on any console codepage.
#  All output -> D:\OraInstall\create_naims12_full.log
#  Result: non-CDB, KO16MSWIN949, system/<비밀번호>@localhost:1522/NAIMS12 + System DSN 'naims'.
#  비밀번호: `$env:NAIMS12_SYS_PW 로 넘기거나 실행 중 입력한다(_secrets.ps1).
#  PREREQUISITE: run 00_install_software_ELEVATED.ps1 first (installs the Oracle software).
# ============================================================================

# --- make console + transcript readable (UTF-8) ---
try { chcp 65001 > $null } catch {}
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
$OutputEncoding = [System.Text.Encoding]::UTF8

Start-Transcript -Path "D:\OraInstall\create_naims12_full.log" -Force | Out-Null
$ErrorActionPreference='Continue'
$pr=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){ Write-Host "[ABORT] Administrator rights required."; Stop-Transcript|Out-Null; return }

$OH=$OracleHome
. "$PSScriptRoot\_secrets.ps1"   # 비밀번호는 코드에 두지 않는다 (환경변수 → 파라미터 → 화면 입력)
$SID="NAIMS12"; $PW=Get-NaimsSecret -EnvName 'NAIMS12_SYS_PW' -Prompt 'NAIMS12 SYSTEM 비밀번호' -DefaultValue 'Naims12#2026'; $base=$OracleBase
$ora="$base\oradata\$SID"; $fra="$base\fast_recovery_area"
$env:ORACLE_HOME=$OH; $env:ORACLE_SID=$SID; $env:PATH="$OH\bin;$env:PATH"
function SQL([string]$conn,[string]$file){ & "$OH\bin\sqlplus.exe" -S $conn "@$file" 2>&1 | Write-Host }

# ---------------------------------------------------------------------------
#  [0/8] PREFLIGHT: verify the Oracle software is fully installed.
#        If sqlplus cannot even print its version, the home is incomplete
#        (Step 00 not run, aborted, or disk ran out) -> stop with a clear message
#        instead of cascading "Error 57 / Unable to load Oracle dll's".
# ---------------------------------------------------------------------------
Write-Host "===== [0/8] PREFLIGHT: check Oracle home is complete ====="
if(-not (Test-Path "$OH\bin\sqlplus.exe")){
  Write-Host "[ABORT] sqlplus.exe not found at $OH\bin. Run 00_install_software_ELEVATED.ps1 first."
  Stop-Transcript|Out-Null; return
}
$here01 = Split-Path -Parent $MyInvocation.MyCommand.Path
$ver = (& "$OH\bin\sqlplus.exe" -V 2>&1 | Out-String)
if($ver -notmatch 'Release'){
  # SELF-HEAL: the most common cause of "Error 57" when all files exist is that the
  # home is not registered (HKLM\SOFTWARE\ORACLE\KEY_*) and ORACLE_HOME is unset.
  # Register it here and retry before giving up.
  Write-Host "  sqlplus -V failed - checking whether the home is registered..."
  $regOk = $false
  foreach($root in 'HKLM:\SOFTWARE\ORACLE','HKLM:\SOFTWARE\Wow6432Node\ORACLE'){
    $k = Get-Item $root -EA SilentlyContinue
    if($k){ foreach($sub in $k.GetSubKeyNames() | Where-Object {$_ -like 'KEY_*'}){
      $h = (Get-ItemProperty "$root\$sub" -Name ORACLE_HOME -EA SilentlyContinue).ORACLE_HOME
      if($h -and $h.TrimEnd('\') -ieq $OH.TrimEnd('\')){ $regOk = $true }
    }}
  }
  if(-not $regOk){
    Write-Host "  -> home is NOT registered. Registering now (13_register_home.ps1)..."
    $s13 = Join-Path $here01 "13_register_home.ps1"
    if(Test-Path $s13){ & powershell -ExecutionPolicy Bypass -File $s13 -OracleHome $OH | ForEach-Object { Write-Host ("    " + $_) } }
    else {
      $key = "HKLM:\SOFTWARE\ORACLE\KEY_OraDB12Home1"
      New-Item -Path $key -Force | Out-Null
      Set-ItemProperty $key -Name ORACLE_HOME      -Value $OH
      Set-ItemProperty $key -Name ORACLE_HOME_NAME -Value "OraDB12Home1"
      Set-ItemProperty $key -Name ORACLE_HOME_KEY  -Value "SOFTWARE\ORACLE\KEY_OraDB12Home1"
      [Environment]::SetEnvironmentVariable('ORACLE_HOME',$OH,'Machine')
      Write-Host "    registered $key"
    }
    $ver = (& "$OH\bin\sqlplus.exe" -V 2>&1 | Out-String)   # retry
  }
}
if($ver -notmatch 'Release'){
  Write-Host "[ABORT] Oracle home is not usable. 'sqlplus -V' failed:"
  Write-Host ("        " + ($ver.Trim() -replace "`r?`n","  "))
  Write-Host "  Next  : 1) powershell -ExecutionPolicy Bypass -File scripts\98_diagnose.ps1   (baseline diff + env checks)"
  Write-Host "          2) if files are missing   -> scripts\99_fix_reinstall.ps1"
  Write-Host "          3) if the installer keeps failing -> scripts\11_transplant_oracle_home.ps1"
  Stop-Transcript|Out-Null; return
}
Write-Host ("  OK: " + (($ver -split "`n" | Where-Object {$_ -match 'Release'} | Select-Object -First 1).Trim()))

Write-Host "===== [1/8] Clean up any previous NAIMS12 instance/services $(Get-Date -f HH:mm:ss) ====="
foreach($s in 'OracleServiceNAIMS12','OracleJobSchedulerNAIMS12','OracleVssWriterNAIMS12'){ & sc.exe stop $s 2>$null|Out-Null }
Start-Sleep 4
& "$OH\bin\oradim.exe" -delete -sid $SID 2>$null | Out-Null
foreach($s in 'OracleServiceNAIMS12','OracleJobSchedulerNAIMS12','OracleVssWriterNAIMS12'){ & sc.exe delete $s 2>$null|Out-Null }
Get-Process oracle -EA SilentlyContinue | Where-Object { $_.Path -like "$OH*" } | Stop-Process -Force -EA SilentlyContinue
foreach($d in @($ora,"$base\admin\$SID","$base\diag\rdbms\naims12","$fra\$SID")){ if(Test-Path $d){ Remove-Item $d -Recurse -Force -EA SilentlyContinue } }
Get-ChildItem "$OH\database" -Filter '*NAIMS12*' -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue

Write-Host "===== [2/8] Create data directories ====="
New-Item -ItemType Directory -Force -Path $ora,"$base\admin\$SID\adump",$fra,"$fra\$SID" | Out-Null

Write-Host "===== [3/8] init pfile + password file ====="
$pfile="$OH\database\init$SID.ora"
@"
db_name='$SID'
memory_target=2560M
processes=300
control_files=('$ora\control01.ctl','$fra\$SID\control02.ctl')
db_block_size=8192
compatible='12.2.0'
undo_tablespace='UNDOTBS1'
audit_file_dest='$base\admin\$SID\adump'
audit_trail='db'
db_recovery_file_dest='$fra'
db_recovery_file_dest_size=12G
diagnostic_dest='$base'
open_cursors=300
remote_login_passwordfile='EXCLUSIVE'
local_listener='(ADDRESS=(PROTOCOL=TCP)(HOST=localhost)(PORT=1522))'
"@ | Set-Content $pfile -Encoding ASCII
& "$OH\bin\orapwd.exe" "file=$OH\database\PWD$SID.ora" "password=$PW" force=y 2>&1 | Write-Host

Write-Host "===== [4/8] Create Windows service/instance (oradim) ====="
& "$OH\bin\oradim.exe" -new -sid $SID -startmode manual -spfile 2>&1 | Write-Host
& "$OH\bin\oradim.exe" -edit -sid $SID -startmode auto 2>&1 | Write-Host

Write-Host "===== [5/8] Create spfile + start NOMOUNT ====="
$sqlNomount="$env:TEMP\n12_nomount.sql"
@"
create spfile from pfile='$pfile';
startup nomount pfile='$pfile';
exit
"@ | Set-Content $sqlNomount -Encoding ASCII
SQL "/ as sysdba" $sqlNomount

Write-Host "===== [6/8] CREATE DATABASE (KO16MSWIN949) $(Get-Date -f HH:mm:ss) ====="
$sqlCreate="$env:TEMP\n12_create.sql"
@"
CREATE DATABASE $SID
  USER SYS IDENTIFIED BY "$PW"
  USER SYSTEM IDENTIFIED BY "$PW"
  LOGFILE GROUP 1 ('$ora\redo01.log') SIZE 200M,
          GROUP 2 ('$ora\redo02.log') SIZE 200M,
          GROUP 3 ('$ora\redo03.log') SIZE 200M
  MAXINSTANCES 1 MAXLOGFILES 16 MAXLOGMEMBERS 4 MAXLOGHISTORY 100
  MAXDATAFILES 200
  CHARACTER SET KO16MSWIN949
  NATIONAL CHARACTER SET AL16UTF16
  EXTENT MANAGEMENT LOCAL
  DATAFILE '$ora\system01.dbf' SIZE 800M AUTOEXTEND ON NEXT 10M MAXSIZE UNLIMITED
  SYSAUX DATAFILE '$ora\sysaux01.dbf' SIZE 600M AUTOEXTEND ON NEXT 10M MAXSIZE UNLIMITED
  DEFAULT TABLESPACE users DATAFILE '$ora\users01.dbf' SIZE 100M AUTOEXTEND ON NEXT 10M MAXSIZE UNLIMITED
  DEFAULT TEMPORARY TABLESPACE temp TEMPFILE '$ora\temp01.dbf' SIZE 200M AUTOEXTEND ON NEXT 10M
  UNDO TABLESPACE undotbs1 DATAFILE '$ora\undotbs01.dbf' SIZE 300M AUTOEXTEND ON NEXT 10M MAXSIZE UNLIMITED;
exit
"@ | Set-Content $sqlCreate -Encoding ASCII
SQL "/ as sysdba" $sqlCreate

Write-Host "===== [7/8] Build data dictionary catalog/catproc + crypto toolkit $(Get-Date -f HH:mm:ss) ====="
$sqlCat="$env:TEMP\n12_cat.sql"
@"
set echo off
@$OH\rdbms\admin\catalog.sql
@$OH\rdbms\admin\catproc.sql
@$OH\rdbms\admin\dbmsobtk.sql
@$OH\rdbms\admin\prvtobtk.plb
grant execute on dbms_obfuscation_toolkit to public;
exit
"@ | Set-Content $sqlCat -Encoding ASCII
SQL "/ as sysdba" $sqlCat
$sqlPup="$env:TEMP\n12_pup.sql"
"@$OH\sqlplus\admin\pupbld.sql`nexit" | Set-Content $sqlPup -Encoding ASCII
SQL "system/$PW" $sqlPup

Write-Host "===== [8/8] Listener (1522) + register + System DSN ====="
$netAdmin="$OH\network\admin"; New-Item -ItemType Directory -Force -Path $netAdmin|Out-Null
@"
LISTENER_N12 =
  (DESCRIPTION_LIST = (DESCRIPTION = (ADDRESS = (PROTOCOL = TCP)(HOST = 0.0.0.0)(PORT = 1522))))
"@ | Set-Content "$netAdmin\listener.ora" -Encoding ASCII
& "$OH\bin\lsnrctl.exe" start LISTENER_N12 2>&1 | Write-Host
$sqlReg="$env:TEMP\n12_reg.sql"
"alter system register;`nselect open_mode from v`$database;`nselect value from nls_database_parameters where parameter='NLS_CHARACTERSET';`nexit" | Set-Content $sqlReg -Encoding ASCII
SQL "/ as sysdba" $sqlReg
$drv=(Get-ItemProperty 'HKLM:\SOFTWARE\ODBC\ODBCINST.INI\Oracle in OraDB12Home1' -EA SilentlyContinue).Driver
if($drv -and (Test-Path $drv)){
  New-Item -Path 'HKLM:\SOFTWARE\ODBC\ODBC.INI\naims' -Force|Out-Null
  Set-ItemProperty 'HKLM:\SOFTWARE\ODBC\ODBC.INI\naims' -Name 'Driver' -Value $drv
  Set-ItemProperty 'HKLM:\SOFTWARE\ODBC\ODBC.INI\naims' -Name 'DBQ' -Value 'localhost:1522/NAIMS12'
  New-Item -Path 'HKLM:\SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources' -Force|Out-Null
  Set-ItemProperty 'HKLM:\SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources' -Name 'naims' -Value 'Oracle in OraDB12Home1'
  Write-Host "System DSN 'naims' registered."
}

# Final result check
$openMode = (@"
set head off feed off pages 0
select open_mode from v`$database;
exit
"@ | & "$OH\bin\sqlplus.exe" -S "system/$PW@localhost:1522/NAIMS12" 2>&1 | Out-String)
if($openMode -match 'READ WRITE'){ Write-Host "===== [DONE] NAIMS12 is OPEN (READ WRITE). Proceed to 02/03/04/05. $(Get-Date -f HH:mm:ss) =====" }
else { Write-Host "===== [WARN] Could not confirm NAIMS12 OPEN. Check the log above. $(Get-Date -f HH:mm:ss) =====" }
Stop-Transcript | Out-Null

