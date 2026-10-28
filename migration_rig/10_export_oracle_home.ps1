# ============================================================================
#  10_export_oracle_home.ps1   -  Export a WORKING Oracle 12c home for transplant.
#  RUN ON THE PC WHERE ORACLE ALREADY WORKS (the dev PC), as Administrator.
#
#  Why: on some W/S the silent installer "succeeds" but leaves a broken home
#  ("Error 57 initializing SQL*Plus"). Instead of fighting the installer, copy a
#  known-good home. A 12c home is relocatable to the SAME path on another PC.
#
#  Produces (default -Out H:\...\Oracle Test Dump 12c\home_image):
#     dbhome_1.zip           the whole Oracle home (~2.5-3 GB)
#     oracle_home.reg        registry: KEY_OraDB12Home1 + ODBC driver entries
#     TRANSPLANT_README.txt  what to run on the W/S
#  Then on the W/S run 11_transplant_oracle_home.ps1.
#  Output is ASCII/English.
# ============================================================================
param(
  [string]$OracleHome = "D:\app\oracle\product\12.2.0\dbhome_1",
  [string]$Out        = "H:\2026.08.10_출장자료\Oracle Test Dump 12c\home_image"
)
try { chcp 65001 > $null } catch {}
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$pr=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(-not $isAdmin){ Write-Host "[NOTE] not elevated: the home will still be zipped; registry export uses HKLM read (usually allowed)." -ForegroundColor Yellow }

Write-Host "===== [1/4] Verify the source home actually works ====="
$sp = Join-Path $OracleHome "bin\sqlplus.exe"
if(-not (Test-Path $sp)){ Write-Host "[ABORT] not found: $sp"; return }
$env:ORACLE_HOME=$OracleHome; $env:PATH="$OracleHome\bin;$env:PATH"
$v = (& $sp -V 2>&1 | Out-String)
if($v -notmatch 'Release'){ Write-Host "[ABORT] source home is NOT working - nothing worth exporting."; Write-Host ("  " + ($v -replace "`r?`n"," ")); return }
Write-Host ("  OK: " + (($v -split "`n" | Where-Object {$_ -match 'Release'} | Select-Object -First 1).Trim()))

New-Item -ItemType Directory -Force -Path $Out | Out-Null

Write-Host "===== [2/4] Export registry (home registration + ODBC driver) ====="
$reg = Join-Path $Out "oracle_home.reg"
$tmp1 = Join-Path $env:TEMP "ora_key.reg"; $tmp2 = Join-Path $env:TEMP "ora_odbc.reg"
& reg.exe export "HKLM\SOFTWARE\ORACLE\KEY_OraDB12Home1" $tmp1 /y | Out-Null
& reg.exe export "HKLM\SOFTWARE\ODBC\ODBCINST.INI\Oracle in OraDB12Home1" $tmp2 /y | Out-Null
$parts = @()
foreach($t in @($tmp1,$tmp2)){ if(Test-Path $t){ $parts += (Get-Content $t -Raw) } }
if($parts.Count){
  # keep one REGEDIT4/Windows Registry Editor header, strip the rest
  $merged = $parts[0]
  for($i=1;$i -lt $parts.Count;$i++){ $merged += "`r`n" + (($parts[$i] -split "`r`n" | Select-Object -Skip 1) -join "`r`n") }
  Set-Content -Path $reg -Value $merged -Encoding Unicode
  Write-Host "  wrote $reg"
} else { Write-Host "  [WARN] registry export produced nothing" }
Remove-Item $tmp1,$tmp2 -Force -EA SilentlyContinue

Write-Host "===== [3/4] Zip the Oracle home (5-6 GB source, takes several minutes) ====="
$zip = Join-Path $Out "dbhome_1.zip"
if(Test-Path $zip){ Remove-Item $zip -Force }
$dst = Split-Path -Parent $zip
$free = (Get-PSDrive ($dst.Substring(0,1)) -EA SilentlyContinue).Free
if($free -and $free -lt 4GB){ Write-Host ("[ABORT] destination has only " + [math]::Round($free/1GB,1) + " GB free; need ~4 GB."); return }
try {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  Add-Type -AssemblyName System.IO.Compression
  $fs = [System.IO.File]::Open($zip,[System.IO.FileMode]::Create)
  $za = New-Object System.IO.Compression.ZipArchive($fs,[System.IO.Compression.ZipArchiveMode]::Create)
  $root = (Resolve-Path $OracleHome).Path.TrimEnd('\') + '\'
  $n=0; $skipped=0; $t0=Get-Date
  Get-ChildItem $OracleHome -Recurse -File -Force -EA SilentlyContinue | ForEach-Object {
    $rel = $_.FullName.Substring($root.Length)
    try {
      $entry = $za.CreateEntry($rel,[System.IO.Compression.CompressionLevel]::Optimal)
      $es = $entry.Open()
      $src = [System.IO.File]::Open($_.FullName,[System.IO.FileMode]::Open,[System.IO.FileAccess]::Read,[System.IO.FileShare]::ReadWrite)
      $src.CopyTo($es); $src.Close(); $es.Close(); $n++
      if($n % 5000 -eq 0){ Write-Host ("    ... $n files, " + [int]((Get-Date)-$t0).TotalSeconds + "s") }
    } catch { $skipped++ }
  }
  $za.Dispose(); $fs.Close()
  Write-Host ("  created " + $zip + " (" + [math]::Round((Get-Item $zip).Length/1GB,2) + " GB) files=$n skipped=$skipped")
  if($skipped -gt 0){ Write-Host ("  [NOTE] $skipped file(s) could not be read (locked/permission). If sqlplus fails after transplant, re-run this elevated.") -ForegroundColor Yellow }
} catch { Write-Host ("[ABORT] zip failed: " + $_.Exception.Message); return }

Write-Host "===== [4/4] Write transplant instructions ====="
@"
TRANSPLANT AN ORACLE 12c HOME (use when the W/S installer keeps producing a broken home)

Source home : $OracleHome
Exported    : dbhome_1.zip (the entire home) + oracle_home.reg (registry)

ON THE W/S (Administrator PowerShell), from the package folder:

  powershell -ExecutionPolicy Bypass -File scripts\11_transplant_oracle_home.ps1

That script will:
  1) clean any half-installed Oracle (services, home, inventory, registry)
  2) extract dbhome_1.zip to EXACTLY $OracleHome   (path must match)
  3) import oracle_home.reg (home registration + ODBC driver)
  4) verify with 'sqlplus -V'
Then continue normally:  01 (create DB) -> 02/03 -> 04 -> 05 -> 99_verify

NOTE: the home must land on the SAME path it came from ($OracleHome),
because Oracle binaries/config contain that path.
"@ | Set-Content (Join-Path $Out "TRANSPLANT_README.txt") -Encoding UTF8

Write-Host "[SUCCESS] export complete -> $Out"
Get-ChildItem $Out | ForEach-Object { Write-Host ("  " + $_.Name + "  " + [math]::Round($_.Length/1MB,1) + " MB") }
