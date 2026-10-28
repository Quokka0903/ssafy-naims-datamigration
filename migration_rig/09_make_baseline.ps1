# ============================================================================
#  09_make_baseline.ps1  -  Capture a fingerprint of a WORKING Oracle 12c home.
#  RUN ON THE PC WHERE ORACLE ALREADY WORKS (dev PC).
#  Writes baseline.json into the package so 98_diagnose.ps1 can DIFF a broken
#  W/S install against a known-good one instead of guessing what is missing.
#
#  Why this exists: several wrong diagnoses were made by assuming which files
#  matter (disk space / oraociei12.dll / zip size). A real baseline removes the
#  guessing - we compare against a home that is proven to run.
#  Output is ASCII/English.
# ============================================================================
param(
  [string]$OracleHome = "D:\app\oracle\product\12.2.0\dbhome_1",
  [string]$Out        = "H:\2026.08.10_출장자료\Oracle Test Dump 12c\scripts\baseline.json"
)
try { chcp 65001 > $null } catch {}
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$sp = Join-Path $OracleHome "bin\sqlplus.exe"
if(-not (Test-Path $sp)){ Write-Host "[ABORT] not found: $sp"; return }
$env:ORACLE_HOME=$OracleHome; $env:PATH="$OracleHome\bin;$env:PATH"
$v = (& $sp -V 2>&1 | Out-String)
if($v -notmatch 'Release'){ Write-Host "[ABORT] this home does not run - it cannot serve as a baseline."; return }
$ver = (($v -split "`n" | Where-Object {$_ -match 'Release'} | Select-Object -First 1)).Trim()
Write-Host "Baseline source: $OracleHome"
Write-Host "  $ver"

# Key runtime pieces that a *working* server home actually has.
# (Determined by inspection of this working home - not assumed.)
$probe = @(
  'bin\sqlplus.exe','bin\oracle.exe','bin\oradim.exe','bin\lsnrctl.exe','bin\orapwd.exe',
  'bin\impdp.exe','bin\expdp.exe','bin\imp.exe','bin\exp.exe',
  'bin\SQORA32.DLL','bin\oraclient12.dll','bin\orasql12.dll','bin\oracommon12.dll',
  'bin\oracore12.dll','bin\orageneric12.dll','bin\oranls12.dll','bin\orapls12.dll',
  'sqlplus\mesg\sp1us.msb','oracore\mesg\lrmus.msb','rdbms\mesg\oraus.msb',
  'rdbms\admin\catalog.sql','rdbms\admin\catproc.sql','rdbms\admin\dbmsobtk.sql'
)
$files = @()
foreach($rel in $probe){
  $p = Join-Path $OracleHome $rel
  if(Test-Path $p){ $files += [pscustomobject]@{ rel=$rel; bytes=(Get-Item $p).Length } }
  else           { $files += [pscustomobject]@{ rel=$rel; bytes=0 } }   # 0 = not present even in a working home
}

$all = Get-ChildItem $OracleHome -Recurse -File -EA SilentlyContinue | Measure-Object -Property Length -Sum
$binCount = (Get-ChildItem (Join-Path $OracleHome 'bin') -File -EA SilentlyContinue | Measure-Object).Count

$obj = [ordered]@{
  capturedFrom   = $env:COMPUTERNAME
  oracleHome     = $OracleHome
  sqlplusVersion = $ver
  totalFiles     = $all.Count
  totalBytes     = $all.Sum
  binFileCount   = $binCount
  files          = $files
}
$dir = Split-Path -Parent $Out
if(-not (Test-Path $dir)){ New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$obj | ConvertTo-Json -Depth 4 | Set-Content $Out -Encoding UTF8
Write-Host ("[SUCCESS] baseline written: $Out")
Write-Host ("  files=" + $all.Count + "  size=" + [math]::Round($all.Sum/1GB,2) + " GB  bin files=" + $binCount)
