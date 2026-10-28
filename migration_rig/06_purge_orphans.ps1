# ════════════════════════════════════════════════════════════════════════════
#  06_purge_orphans.ps1  —  레거시 소스의 고아 링크 레코드 정리
#
#  무엇을 지우는가
#    부모가 존재하지 않는 "링크(다대다 매핑) 테이블" 행만 지운다.
#    가리키는 대상이 이미 사라졌으므로 관계로서 의미가 없고, 이관하면 FK 를 채울 수 없다.
#
#  무엇을 지우지 않는가
#    계층 테이블(CLASSES/VESSEL_DATA/SIGNATURES/HARMONICS/NB_SNIPPET/TR_FRAME …)은
#    이 스크립트가 절대 건드리지 않는다. 계층 쪽 고아는 발견하면 보고만 하고 멈춘다 —
#    그건 링크 정리와 성격이 다르고(자료 유실), 사람이 판단할 일이다.
#
#  실측(2026-08-19, 로컬 리허설 NAIMS12)
#    SIG_SNIPPET    9건  — 5건은 NB_SNIPPET 삭제 후 남은 실데이터 링크,
#                          4건은 SIGNATURE_ID 100000001/100000002 를 가리키는데 그 부모가 없음
#    FRAME_SNIPPET  1건  — FRAME_ID=29
#    계층 테이블 고아 0건 (CLASS→VESSEL→SIGNATURES→HARMONICS 전부 무결)
#
#  ⚠ 안전 원칙 — 2026-08-06 실서버 오삭제 사고 이후 규칙
#    · 기본은 점검만. 실제 삭제는 -Apply 를 붙여야 한다.
#    · -Apply 에는 -ConfirmTarget 이 반드시 함께 와야 하고, 접속 문자열의 DBQ 와
#      글자 그대로 같아야 한다. 다르면 아무것도 하지 않고 종료한다.
#    · 삭제 전 대상 행 전체를 CSV 로 백업한다(백업 실패 시 삭제하지 않는다).
#    · 트랜잭션으로 묶어 중간 실패 시 전부 되돌린다.
#
#  사용 예
#    powershell -ExecutionPolicy Bypass -File 06_purge_orphans.ps1
#    powershell -ExecutionPolicy Bypass -File 06_purge_orphans.ps1 `
#        -Apply -ConfirmTarget 'localhost:1522/NAIMS12'
# ════════════════════════════════════════════════════════════════════════════
param(
    [string]$Dbq           = 'localhost:1522/NAIMS12',
    [string]$Driver        = 'Oracle in OraDB12Home1',
    [string]$Uid           = 'LEGACYNAIMS',
    [string]$Pwd           = '',
    [switch]$Apply,
    [string]$ConfirmTarget = '',
    [string]$BackupDir     = "$PSScriptRoot\_orphan_backup"
)

$ErrorActionPreference = 'Stop'
function Say($m) { Write-Host $m }

# 링크 테이블만 — (자식표, 자식PK, FK컬럼, 부모표, 부모PK)
$LINKS = @(
    @{ Child='SIG_SNIPPET';   Pk='SIG_SNP_ID'; Fk='SIGNATURE_ID';  Parent='SIGNATURES'; ParentPk='SIGNATURE_ID'  },
    @{ Child='SIG_SNIPPET';   Pk='SIG_SNP_ID'; Fk='NB_SNIPPET_ID'; Parent='NB_SNIPPET'; ParentPk='NB_SNIPPET_ID' },
    @{ Child='FRAME_SNIPPET'; Pk='FRM_SNP_ID'; Fk='FRAME_ID';      Parent='TR_FRAME';   ParentPk='FRAME_ID'      },
    @{ Child='FRAME_SNIPPET'; Pk='FRM_SNP_ID'; Fk='TR_SNIPPET_ID'; Parent='TR_SNIPPET'; ParentPk='TR_SNIPPET_ID' }
)

# 계층 테이블 — 보고만 한다(절대 삭제 대상 아님)
$HIER = @(
    @{ Child='VESSEL_DATA'; Fk='CLASS_ID';      Parent='CLASSES';     ParentPk='CLASS_ID'     },
    @{ Child='SIGNATURES';  Fk='VESSEL_ID';     Parent='VESSEL_DATA'; ParentPk='VESSEL_ID'    },
    @{ Child='HARMONICS';   Fk='SIGNATURES_ID'; Parent='SIGNATURES';  ParentPk='SIGNATURE_ID' },
    @{ Child='TR_FRAME';    Fk='TR_ID';         Parent='TRANSIENT';   ParentPk='TR_ID'        }
)

# ── 접속 ───────────────────────────────────────────────────────────────────
if (-not $Pwd) {
    $Pwd = if ($env:LEGACYNAIMS_PW) { $env:LEGACYNAIMS_PW } else { 'naims2!' }
}
$cs = "Driver={$Driver};DBQ=$Dbq;Uid=$Uid;Pwd=$Pwd;"

$mode = if ($Apply) { '실제 삭제' } else { '점검만 (변경 없음 — 지우려면 -Apply -ConfirmTarget)' }
Say "===== 06 고아 링크 정리 ====="
Say "  대상 DB : $Dbq"
Say "  계정    : $Uid"
Say "  모드    : $mode"
Say ""

# ── 대상 확인 (파괴적 작업 방어) ───────────────────────────────────────────
if ($Apply) {
    if (-not $ConfirmTarget) {
        Say "!! -Apply 에는 -ConfirmTarget 이 필요합니다."
        Say "   지우려는 DB 를 그대로 적어 주세요:  -ConfirmTarget '$Dbq'"
        exit 1
    }
    if ($ConfirmTarget -ne $Dbq) {
        Say "!! 대상 불일치 — 아무것도 하지 않고 종료합니다."
        Say "   -Dbq           : $Dbq"
        Say "   -ConfirmTarget : $ConfirmTarget"
        exit 1
    }
}

$conn = New-Object System.Data.Odbc.OdbcConnection($cs)
$conn.Open()
Say "접속 성공 — $($conn.ServerVersion)"
Say ""

function Rows($sql) {
    $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql
    $r = $cmd.ExecuteReader()
    $out = @()
    while ($r.Read()) {
        $o = [ordered]@{}
        for ($i = 0; $i -lt $r.FieldCount; $i++) { $o[$r.GetName($i)] = $r.GetValue($i) }
        $out += [pscustomobject]$o
    }
    $r.Close()
    return $out
}

# ── 1) 계층 점검 — 여기 고아가 있으면 멈춘다 ───────────────────────────────
Say "── 계층 테이블 점검 (삭제 대상 아님) ──"
$hierBad = 0
foreach ($h in $HIER) {
    $n = (Rows "SELECT COUNT(*) AS N FROM $($h.Child) c WHERE NOT EXISTS (SELECT 1 FROM $($h.Parent) p WHERE p.$($h.ParentPk)=c.$($h.Fk))")[0].N
    Say ("  {0,-42} 고아 {1}" -f "$($h.Child).$($h.Fk) → $($h.Parent)", $n)
    $hierBad += [int]$n
}
if ($hierBad -gt 0) {
    Say ""
    Say "!! 계층 테이블에 고아가 $hierBad 건 있습니다. 이 스크립트는 계층을 건드리지 않으므로 중단합니다."
    Say "   자료 유실이 얽힌 문제라 사람이 판단해야 합니다."
    $conn.Close(); exit 1
}
Say ""

# ── 2) 링크 고아 수집 ──────────────────────────────────────────────────────
Say "── 링크 테이블 고아 ──"
$targets = @{}   # "테이블" -> PK 집합
$detail  = @()
foreach ($l in $LINKS) {
    $rows = Rows @"
SELECT c.$($l.Pk) AS CHILD_PK, c.$($l.Fk) AS FK_VALUE
FROM $($l.Child) c
WHERE NOT EXISTS (SELECT 1 FROM $($l.Parent) p WHERE p.$($l.ParentPk)=c.$($l.Fk))
ORDER BY c.$($l.Pk)
"@
    Say ("  {0,-46} {1}건" -f "$($l.Child).$($l.Fk) → $($l.Parent)", $rows.Count)
    foreach ($r in $rows) {
        if (-not $targets.ContainsKey($l.Child)) { $targets[$l.Child] = @{ Pk = $l.Pk; Ids = New-Object 'System.Collections.Generic.HashSet[string]' } }
        [void]$targets[$l.Child].Ids.Add("$($r.CHILD_PK)")
        $detail += [pscustomobject]@{
            Child = $l.Child; ChildPk = $r.CHILD_PK; FkColumn = $l.Fk
            FkValue = $r.FK_VALUE; MissingParent = $l.Parent
        }
    }
}
$total = ($targets.Values | ForEach-Object { $_.Ids.Count } | Measure-Object -Sum).Sum
if (-not $total) { $total = 0 }
Say ""
Say "삭제 대상 행: $total (링크 참조 $($detail.Count)건)"
if ($total -eq 0) { Say "정리할 것이 없습니다."; $conn.Close(); exit 0 }

Say ""
Say "── 상세 ──"
$detail | ForEach-Object {
    Say ("  {0,-14} PK={1,-12} {2}={3,-12} (없는 부모: {4})" -f $_.Child, $_.ChildPk, $_.FkColumn, $_.FkValue, $_.MissingParent)
}

if (-not $Apply) {
    Say ""
    Say "※ 점검만 했습니다. 실제로 지우려면:"
    Say "   -Apply -ConfirmTarget '$Dbq'"
    $conn.Close(); exit 0
}

# ── 3) 백업 (실패하면 삭제하지 않는다) ─────────────────────────────────────
New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$backupOk = $true
foreach ($t in $targets.Keys) {
    $pk  = $targets[$t].Pk
    $ids = ($targets[$t].Ids) -join ','
    try {
        $full = Rows "SELECT * FROM $t WHERE $pk IN ($ids)"
        $out  = Join-Path $BackupDir "${t}_orphans_$stamp.csv"
        $full | Export-Csv -Path $out -NoTypeInformation -Encoding UTF8
        Say "  [백업] $out  ($($full.Count)행)"
    }
    catch { Say "  !! 백업 실패 ($t): $($_.Exception.Message)"; $backupOk = $false }
}
$detail | Export-Csv -Path (Join-Path $BackupDir "orphan_index_$stamp.csv") -NoTypeInformation -Encoding UTF8

if (-not $backupOk) {
    Say ""
    Say "!! 백업에 실패해 삭제를 중단합니다(되돌릴 수단 없이 지우지 않습니다)."
    $conn.Close(); exit 1
}

# ── 4) 삭제 (트랜잭션) ─────────────────────────────────────────────────────
Say ""
Say "── 삭제 ──"
$tx = $conn.BeginTransaction()
try {
    $deleted = 0
    foreach ($t in $targets.Keys) {
        $pk  = $targets[$t].Pk
        $ids = ($targets[$t].Ids) -join ','
        $cmd = $conn.CreateCommand(); $cmd.Transaction = $tx
        $cmd.CommandText = "DELETE FROM $t WHERE $pk IN ($ids)"
        $n = $cmd.ExecuteNonQuery()
        Say "  $t : $n 행 삭제"
        $deleted += $n
    }
    $tx.Commit()
    Say "커밋 완료 — 총 $deleted 행"
}
catch {
    $tx.Rollback()
    Say "!! 삭제 실패 — 전부 되돌렸습니다: $($_.Exception.Message)"
    $conn.Close(); exit 1
}

# ── 5) 검증 ────────────────────────────────────────────────────────────────
Say ""
Say "── 검증 (재조회) ──"
$left = 0
foreach ($l in $LINKS) {
    $n = (Rows "SELECT COUNT(*) AS N FROM $($l.Child) c WHERE NOT EXISTS (SELECT 1 FROM $($l.Parent) p WHERE p.$($l.ParentPk)=c.$($l.Fk))")[0].N
    Say ("  {0,-46} {1}건" -f "$($l.Child).$($l.Fk) → $($l.Parent)", $n)
    $left += [int]$n
}
Say ""
Say $(if ($left -eq 0) { "✅ 고아 링크 0건 — 정리 완료. 백업: $BackupDir" }
      else { "⚠ 아직 $left 건 남았습니다 — 위 상세를 확인하세요." })
$conn.Close()
