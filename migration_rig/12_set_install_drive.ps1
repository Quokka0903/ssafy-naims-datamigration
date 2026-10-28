# ============================================================================
#  12_set_install_drive.ps1   *** RUN AS ADMINISTRATOR (W/S) ***
#  Retarget the whole package to a DIFFERENT drive/path in one shot.
#
#  Use when 98_diagnose.ps1 says the intended volume is unsuitable, e.g.
#    - "D: is NTFS  -> FAIL (exFAT/FAT32)"     : Oracle needs NTFS
#    - "D: is a Fixed disk -> FAIL (Removable)": USB volumes half-copy multi-GB installs
#    - ORACLE_HOME folder is NTFS-COMPRESSED
#    - or there is simply no D: drive on this W/S
#
#  It picks (or takes) a target drive, validates it (NTFS + Fixed + free space),
#  and rewrites every place the old path is hard-coded:
#      db_install_naims12.rsp   (ORACLE_HOME / ORACLE_BASE)
#      00_install_software_ELEVATED.ps1  ($OH, $extract)
#      01_install_and_createdb_ELEVATED.ps1 (param defaults)
#      04_restore_dump.ps1, 04b, 05b, 95, 96, 98, 99_fix_reinstall, 11
#  Backups are written next to each file as *.bak.
#  Output is ASCII/English.
#
#    powershell -ExecutionPolicy Bypass -File scripts\12_set_install_drive.ps1            (auto-pick)
#    powershell -ExecutionPolicy Bypass -File scripts\12_set_install_drive.ps1 -Drive E   (explicit)
# ============================================================================
param(
  [string]$Drive = "",        # e.g. "C" or "E". empty = auto-pick the best suitable volume
  [switch]$WhatIfOnly         # only show what would change
)
try { chcp 65001 > $null } catch {}
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$oldHome = "D:\app\oracle\product\12.2.0\dbhome_1"
$oldBase = "D:\app\oracle"
$oldStage= "D:\OraInstall"

function Suitable($v){
  if(-not $v){ return $false }
  if($v.FileSystem -ne 'NTFS'){ return $false }
  if($v.DriveType -ne 'Fixed'){ return $false }
  if($v.SizeRemaining -lt 20GB){ return $false }
  return $true
}

Write-Host "===== [1/3] Choose a target volume ====="
Write-Host "  (requirements: NTFS + Fixed disk + 20GB+ free)"
$vols = Get-Volume | Where-Object { $_.DriveLetter } | Sort-Object -Property @{e={$_.SizeRemaining}} -Descending
foreach($v in $vols){
  $ok = Suitable $v
  Write-Host ("   {0}: {1,-6} {2,-9} free={3,7} GB  {4}" -f $v.DriveLetter,$v.FileSystem,$v.DriveType,[math]::Round($v.SizeRemaining/1GB,1),$(if($ok){'SUITABLE'}else{'not suitable'}))
}
if($Drive){
  $target = $vols | Where-Object { $_.DriveLetter -eq $Drive.TrimEnd(':') } | Select-Object -First 1
  if(-not $target){ Write-Host "[ABORT] drive $Drive not found."; return }
  if(-not (Suitable $target)){ Write-Host "[ABORT] drive $Drive is not suitable (needs NTFS + Fixed + 20GB free)."; return }
} else {
  $target = $vols | Where-Object { Suitable $_ } | Select-Object -First 1
  if(-not $target){ Write-Host "[ABORT] no suitable volume found on this machine (need NTFS + Fixed + 20GB free)."; return }
}
$D = [string]$target.DriveLetter
Write-Host ("  -> selected: ${D}:  (" + $target.FileSystem + ", " + $target.DriveType + ", " + [math]::Round($target.SizeRemaining/1GB,1) + " GB free)")

$newHome = "${D}:\app\oracle\product\12.2.0\dbhome_1"
$newBase = "${D}:\app\oracle"
$newStage= "${D}:\OraInstall"
if($newHome -ieq $oldHome){ Write-Host "[DONE] already targeting ${D}: - nothing to change."; return }

Write-Host "===== [2/3] Rewrite hard-coded paths ====="
$targets = @('db_install_naims12.rsp','00_install_software_ELEVATED.ps1','01_install_and_createdb_ELEVATED.ps1',
             '04_restore_dump.ps1','04b_restore_anydump.ps1','05b_fix_dsn.ps1','95_check_existing_oracle.ps1',
             '96_start_db_listener.ps1','98_diagnose.ps1','99_fix_reinstall.ps1','11_transplant_oracle_home.ps1')
$changed=0
foreach($f in $targets){
  $p = Join-Path $here $f
  if(-not (Test-Path $p)){ continue }
  $txt = Get-Content $p -Raw
  $new = $txt.Replace($oldHome,$newHome).Replace($oldBase,$newBase).Replace($oldStage,$newStage)
  if($new -ne $txt){
    if($WhatIfOnly){ Write-Host "  would change: $f" }
    else {
      Copy-Item $p "$p.bak" -Force
      Set-Content $p -Value $new -Encoding UTF8
      Write-Host "  updated: $f  (backup: $f.bak)"
    }
    $changed++
  }
}
Write-Host ("  files changed: $changed")
if($WhatIfOnly){ Write-Host "[WhatIfOnly] nothing written."; return }

Write-Host "===== [3/3] Next steps ====="
Write-Host "  New ORACLE_HOME = $newHome"
Write-Host "  New ORACLE_BASE = $newBase"
Write-Host "  Installer staging = $newStage\db12201"
Write-Host ""
Write-Host "  1) powershell -ExecutionPolicy Bypass -File scripts\99_fix_reinstall.ps1     (clean + install on ${D}:)"
Write-Host "     or, if you have a home image:"
Write-Host "     powershell -ExecutionPolicy Bypass -File scripts\11_transplant_oracle_home.ps1 -OracleHome `"$newHome`""
Write-Host "     NOTE: a transplanted home must go to the SAME path it was exported from."
Write-Host "           If the image came from D:, either keep D: or re-export from a PC using ${D}:."
Write-Host "  2) powershell -ExecutionPolicy Bypass -File scripts\01_install_and_createdb_ELEVATED.ps1"
Write-Host "  3) 02/03 -> 04 -> 05 -> 99_verify"
