# ════════════════════════════════════════════════════════════════════════════
#  05c_repair_dsn.ps1  —  ODBC DSN 등록 정합성 복구
#
#  왜 필요한가 (2026-08-19 실측):
#    이 PC 에서 DSN=naims 로 붙으면 IM002("데이터 원본 이름이 없고 기본 드라이버를
#    지정하지 않았습니다")가 산발적으로 났다. 드라이버는 멀쩡했다 — DSN 을 빼고
#    Driver={...};DBQ=... 로 붙이면 항상 성공했다. 원인은 DSN '등록 상태' 쪽이었다.
#
#      ① 목록/서브키 불일치
#         HKCU\...\ODBC.INI\ODBC Data Sources 목록에는 없는데 DSN 서브키만 남은
#         '고아' 항목들이 있었다(naims, XEPDB1 …). ODBC 드라이버 관리자는 HKCU 를
#         먼저 조회하므로, HKLM 에 제대로 등록된 같은 이름을 HKCU 고아가 가린다.
#      ② Driver 값 형식
#         DSN 의 Driver 에는 ODBCINST.INI 의 '이름'(예: Oracle in OraDB12Home1)이
#         들어가야 하는데 DLL 전체 경로가 들어 있었다. 레지스트리에 직접 쓰면 이렇게 된다.
#
#  이 스크립트가 하는 일
#    1) HKCU/HKLM 의 ODBC.INI 를 .reg 로 백업
#    2) '가려짐' 고아만 정밀 제거 — 같은 이름이 다른 하이브에 정상 등록돼 있는 경우에만.
#       그 외 고아(Excel Files 등)는 건드리지 않고 보고만 한다.
#    3) odbcconf.exe(정식 API)로 시스템 DSN 재등록 → Driver 가 이름 형식으로 들어간다
#    4) 열거·실제 접속으로 검증
#
#  사용 예
#    powershell -ExecutionPolicy Bypass -File 05c_repair_dsn.ps1                 # 점검만(WhatIf)
#    powershell -ExecutionPolicy Bypass -File 05c_repair_dsn.ps1 -Apply          # 실제 적용
#    (시스템 DSN 재등록은 관리자 권한 필요 — 없으면 고아 정리까지만 하고 안내한다)
# ════════════════════════════════════════════════════════════════════════════
param(
    [string]$DsnName   = 'naims',
    [string]$Driver    = 'Oracle in OraDB12Home1',
    [string]$Dbq       = 'localhost:1522/NAIMS12',
    [string]$Uid       = 'LEGACYNAIMS',
    [switch]$Apply,                                   # 없으면 점검만 하고 아무것도 바꾸지 않는다
    [switch]$RemoveAllOrphans,                        # 가려지지 않은 고아까지 전부 제거
    [string]$BackupDir = "$PSScriptRoot\_odbc_backup"
)

$ErrorActionPreference = 'Stop'
function Say($m) { Write-Host $m }
$mode = if ($Apply) { '적용' } else { '점검만 (실제 변경 없음 — 적용하려면 -Apply)' }
Say "===== 05c ODBC DSN 복구 =====  모드: $mode"

$hives = @(
    @{ Key = 'HKCU:\SOFTWARE\ODBC\ODBC.INI'; Label = '사용자' },
    @{ Key = 'HKLM:\SOFTWARE\ODBC\ODBC.INI'; Label = '시스템' }
)

# ── 1) 백업 ────────────────────────────────────────────────────────────────
if ($Apply) {
    New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    foreach ($h in $hives) {
        $regPath = $h.Key -replace '^HKCU:', 'HKCU' -replace '^HKLM:', 'HKLM'
        $out = Join-Path $BackupDir "odbcini_$($h.Label)_$stamp.reg"
        & reg.exe export $regPath $out /y | Out-Null
        if (Test-Path $out) { Say "  [백업] $out" }
    }
}

# ── 2) 목록 vs 서브키 대조 ─────────────────────────────────────────────────
$state = @{}
foreach ($h in $hives) {
    $listPath = Join-Path $h.Key 'ODBC Data Sources'
    $listed = @()
    if (Test-Path $listPath) {
        $listed = (Get-ItemProperty $listPath).PSObject.Properties |
                  Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object { $_.Name }
    }
    $subkeys = @()
    if (Test-Path $h.Key) {
        $subkeys = Get-ChildItem $h.Key -EA SilentlyContinue |
                   Where-Object { $_.PSChildName -ne 'ODBC Data Sources' } |
                   ForEach-Object { $_.PSChildName }
    }
    $state[$h.Label] = @{ Key = $h.Key; Listed = $listed; Subkeys = $subkeys }
    Say "`n[$($h.Label)] 목록 $($listed.Count)개 / 서브키 $($subkeys.Count)개"
    foreach ($n in $listed) { Say "   목록에 있음 : $n" }
}

# 고아 = 서브키는 있는데 같은 하이브 목록에 없음
$removed = 0; $kept = 0
foreach ($label in $state.Keys) {
    $s = $state[$label]
    $orphans = $s.Subkeys | Where-Object { $s.Listed -notcontains $_ }
    foreach ($o in $orphans) {
        # 다른 하이브에 '정상 등록'(목록+서브키)돼 있으면 이 고아가 그것을 가린다 → 제거 대상
        $shadows = $false
        foreach ($other in $state.Keys) {
            if ($other -eq $label) { continue }
            if ($state[$other].Listed -contains $o) { $shadows = $true }
        }
        $target = Join-Path $s.Key $o
        if ($shadows -or $RemoveAllOrphans) {
            $why = if ($shadows) { "다른 하이브의 정상 DSN 을 가림" } else { "-RemoveAllOrphans" }
            Say "   ★제거 대상 [$label] $o   ($why)"
            if ($Apply) { Remove-Item $target -Recurse -Force; $removed++ }
        }
        else {
            Say "   · 고아지만 유지 [$label] $o   (가리는 대상 없음 — 건드리지 않음)"
            $kept++
        }
    }
}
Say "`n고아 제거 $removed 건 / 유지 $kept 건"

# ── 3) 정식 API 로 시스템 DSN 재등록 ───────────────────────────────────────
$pr = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($Apply -and $isAdmin) {
    # odbcconf 는 속성을 | 로 구분한다. 이 경로로 등록하면 Driver 가 '이름' 형식으로 들어간다.
    $attrs = "DSN=$DsnName|DBQ=$Dbq|UID=$Uid"
    Say "`n[재등록] odbcconf CONFIGSYSDSN `"$Driver`" `"$attrs`""
    & odbcconf.exe /A "{CONFIGSYSDSN `"$Driver`" `"$attrs`"}" | Out-Null
    Say "  종료코드 $LASTEXITCODE"
}
elseif ($Apply) {
    Say "`n[재등록] 건너뜀 — 시스템 DSN 등록에는 관리자 권한이 필요합니다."
    Say "         관리자 PowerShell 에서 이 스크립트를 -Apply 로 다시 실행하세요."
}

# ── 4) 검증 ────────────────────────────────────────────────────────────────
Say "`n===== 검증 ====="
foreach ($h in $hives) {
    $sub = Join-Path $h.Key $DsnName
    if (Test-Path $sub) {
        $d = Get-ItemProperty $sub
        Say "  [$($h.Label)] $DsnName  Driver=$($d.Driver)  DBQ=$($d.DBQ)"
    }
}
try {
    $cs = "DSN=$DsnName;Uid=$Uid;Pwd=$(if ($env:LEGACYNAIMS_PW) { $env:LEGACYNAIMS_PW } else { 'naims2!' });"
    $c = New-Object System.Data.Odbc.OdbcConnection($cs)
    $c.Open(); Say "  접속 검증: 성공 ($($c.ServerVersion))"; $c.Close()
}
catch { Say "  접속 검증: 실패 — $($_.Exception.Message.Split([char]10)[0])" }

if (-not $Apply) { Say "`n※ 점검만 했습니다. 실제로 고치려면 -Apply 를 붙여 다시 실행하세요." }
