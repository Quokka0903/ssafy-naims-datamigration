# ════════════════════════════════════════════════════════════════════════════
#  07_prepare_migtest_db.ps1  —  로컬 이관 시험용 DB 준비
#
#  하는 일 (사용자 지시, 2026-08-19)
#    1) 대상 3개 DB 를 먼저 백업한다 (DROP 은 되돌릴 수 없다)
#    2) N2_Join_Test  DROP      — 이관과 무관
#    3) NAIMS2_DUMP   DROP      — 컬럼 구조가 현행과 다름(1971 vs 2063)이라 대체 불가
#    4) NAIMS2        전 테이블 레코드 삭제 + IDENTITY 리시드
#    5) NAIMS2 → NAIMS2_DBMig_Test 로 개명
#
#  ⚠ 안전 원칙
#    · 로컬(localhost) 인스턴스만 대상으로 한다. 원격 서버는 -Server 로 지정해도 거부한다.
#    · 백업에 실패하면 그 DB 는 건드리지 않는다.
#    · 기본은 점검만. 실제 실행은 -Apply 가 필요하다.
#    · 삭제는 계획에 적힌 DB 이름 3개로 한정한다(패턴 매칭·전체 순회 없음).
#
#  사용 예
#    powershell -ExecutionPolicy Bypass -File 07_prepare_migtest_db.ps1
#    powershell -ExecutionPolicy Bypass -File 07_prepare_migtest_db.ps1 -Apply
# ════════════════════════════════════════════════════════════════════════════
param(
    [string]$Server    = 'localhost',
    [string[]]$Drop    = @('N2_Join_Test', 'NAIMS2_DUMP'),
    [string]$Keep      = 'NAIMS2',
    [string]$NewName   = 'NAIMS2_DBMig_Test',
    [string]$BackupDir = 'D:\_sql_backup_predrop',
    [switch]$Apply,
    # 원격 서버를 다루려면 두 가지가 모두 있어야 한다 — 실수로 원격을 지우는 일을 막는다.
    [switch]$AllowRemote,
    [string]$ConfirmServer = ''
)

$ErrorActionPreference = 'Stop'
function Say($m) { Write-Host $m }

# ── 로컬 확인 ──────────────────────────────────────────────────────────────
$host0 = $Server.Split('\')[0].Split(',')[0].Trim()
$isLocal = $host0 -in @('.', '(local)', 'localhost', '127.0.0.1', '::1', $env:COMPUTERNAME)
if (-not $isLocal) {
    if (-not $AllowRemote) {
        Say "!! '$Server' 는 이 PC 가 아닙니다. 원격을 다루려면 -AllowRemote 와 -ConfirmServer 가 필요합니다."
        exit 1
    }
    if ($ConfirmServer -ne $Server) {
        Say "!! 원격 서버 확인 불일치 — 아무것도 하지 않습니다."
        Say "   -Server        : $Server"
        Say "   -ConfirmServer : $ConfirmServer"
        exit 1
    }
    Say "※ 원격 서버 대상 — 확인 통과 ($Server)"
}

$cs = "Server=$Server;Database=master;Trusted_Connection=yes;Encrypt=false;TrustServerCertificate=true;Connect Timeout=15"
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

function Exec($sql, [int]$timeout = 600) {
    $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = $timeout
    return $cmd.ExecuteNonQuery()
}
function Scalar($sql) {
    $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 120
    return $cmd.ExecuteScalar()
}
function DbExists($n) { return [int](Scalar "SELECT COUNT(*) FROM sys.databases WHERE name = N'$n'") -gt 0 }

$mode = if ($Apply) { '실제 실행' } else { '점검만 (변경 없음 — 실행하려면 -Apply)' }
Say "===== 07 이관 시험 DB 준비 ====="
Say "  서버 : $Server  ($((Scalar 'SELECT @@VERSION').Split([char]10)[0]))"
Say "  모드 : $mode"
Say ""

Say "── 현재 상태 ──"
$showList = @($Drop); if ($Keep) { $showList += $Keep }; if ($NewName) { $showList += $NewName }
foreach ($n in $showList) {
    if (DbExists $n) {
        $rows = Scalar "SELECT ISNULL(SUM(p.rows),0) FROM [$n].sys.tables t JOIN [$n].sys.partitions p ON p.object_id=t.object_id AND p.index_id IN (0,1)"
        Say ("  {0,-20} 있음   총 {1} 행" -f $n, $rows)
    } else {
        Say ("  {0,-20} 없음" -f $n)
    }
}
Say ""

if (-not $Apply) {
    Say "계획:"
    foreach ($n in $Drop) { Say "  · $n  → 백업 후 DROP" }
    if ($Keep) { Say "  · $Keep → 백업 후 전 테이블 레코드 삭제 + IDENTITY 리시드 → '$NewName' 로 개명" }
    Say ""
    Say "※ 점검만 했습니다. 실행하려면 -Apply 를 붙이세요."
    $conn.Close(); exit 0
}

# ── 1) 백업 ────────────────────────────────────────────────────────────────
if ($isLocal) {
    New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
} else {
    # BACKUP 은 서버 쪽 디스크에 쓴다 — 이 PC 경로를 주면 실패한다. 인스턴스 기본 백업 폴더를 쓴다.
    # SERVERPROPERTY('InstanceDefaultBackupPath') 는 SQL 2019+ 에만 있다.
    # 2016 은 DBNull 을 돌려주는데, PowerShell 에서 [DBNull] 은 참으로 평가되므로
    # -not 검사로는 걸러지지 않는다 → 레지스트리 조회로 넘어가지 못하고 TrimEnd 에서 터졌다.
    function NullSafe($v) { if ($null -eq $v -or $v -is [DBNull]) { return '' } return [string]$v }
    $srvBak = NullSafe (Scalar "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))")
    if (-not $srvBak) {
        $regSql = "DECLARE @p nvarchar(4000); EXEC master.dbo.xp_instance_regread " +
                  "N'HKEY_LOCAL_MACHINE', N'Software\Microsoft\MSSQLServer\MSSQLServer', " +
                  "N'BackupDirectory', @p OUTPUT; SELECT @p"
        $srvBak = NullSafe (Scalar $regSql)
    }
    if (-not $srvBak) { Say "!! 원격 서버의 백업 경로를 알 수 없습니다. 중단합니다."; $conn.Close(); exit 1 }
    $BackupDir = $srvBak.TrimEnd([char]92)
    Say "  (원격 백업 경로: $BackupDir)"
}
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$backedUp = @{}
Say "── 백업 (되돌릴 수단 확보) ──"
$backupList = @($Drop); if ($Keep) { $backupList += $Keep }
foreach ($n in $backupList) {
    if (-not (DbExists $n)) { Say "  · $n 없음 — 건너뜀"; continue }
    $file = Join-Path $BackupDir "${n}_$stamp.bak"
    try {
        Exec "BACKUP DATABASE [$n] TO DISK = N'$file' WITH INIT, COMPRESSION, STATS = 25" 3600 | Out-Null
        $mb = [math]::Round((Get-Item $file).Length / 1MB, 1)
        Say "  [백업] $file  ($mb MB)"
        $backedUp[$n] = $true
    }
    catch {
        Say "  !! 백업 실패 ($n): $($_.Exception.Message.Split([char]10)[0])"
        Say "     → 이 DB 는 건드리지 않습니다."
        $backedUp[$n] = $false
    }
}
Say ""

# ── 2) DROP ────────────────────────────────────────────────────────────────
Say "── DROP ──"
foreach ($n in $Drop) {
    if (-not (DbExists $n)) { Say "  · $n 없음 — 건너뜀"; continue }
    if (-not $backedUp[$n]) { Say "  !! $n 백업이 없어 DROP 하지 않습니다."; continue }
    Exec "ALTER DATABASE [$n] SET SINGLE_USER WITH ROLLBACK IMMEDIATE" | Out-Null
    Exec "DROP DATABASE [$n]" | Out-Null
    Say "  · $n DROP 완료"
}
Say ""

# ── 3) NAIMS2 비우기 ───────────────────────────────────────────────────────
if (-not $Keep) {
    Say "── 비우기·개명 단계 없음 (-Keep 미지정) ──"
    Say ""
    Say "── 검증 ──"
    $cmdV = $conn.CreateCommand(); $cmdV.CommandText = "SELECT name, state_desc FROM sys.databases WHERE database_id > 4 ORDER BY name"
    $rv = $cmdV.ExecuteReader(); while ($rv.Read()) { Say ("  {0,-24} {1}" -f $rv[0], $rv[1]) }; $rv.Close()
    Say ""; Say "백업 위치: $BackupDir"
    $conn.Close(); exit 0
}
if (-not (DbExists $Keep)) { Say "!! '$Keep' 가 없습니다. 중단합니다."; $conn.Close(); exit 1 }
if (-not $backedUp[$Keep]) { Say "!! '$Keep' 백업이 없어 비우지 않습니다."; $conn.Close(); exit 1 }

Say "── $Keep 레코드 비우기 ──"
$before = Scalar "SELECT ISNULL(SUM(p.rows),0) FROM [$Keep].sys.tables t JOIN [$Keep].sys.partitions p ON p.object_id=t.object_id AND p.index_id IN (0,1)"
Say "  이전 총 행수: $before"

# [NAIMS-ISSUE:QUOTED-IDENT] sp_MSforeachtable 은 QUOTED_IDENTIFIER OFF 로 만들어져 있어,
#   필터드 인덱스·계산열이 있는 테이블에서 DELETE 가 거부된다("SET 옵션이 잘못되어 DELETE 실패").
#   그래서 순회를 PowerShell 이 직접 한다 — 우리 연결은 QUOTED_IDENTIFIER 가 ON 이다.
$cmdT = $conn.CreateCommand()
$cmdT.CommandText = "SELECT s.name, t.name FROM [$Keep].sys.tables t JOIN [$Keep].sys.schemas s ON s.schema_id=t.schema_id ORDER BY s.name, t.name"
$rt = $cmdT.ExecuteReader()
$tbls = @()
while ($rt.Read()) { $tbls += ,@($rt[0], $rt[1]) }
$rt.Close()
Say "  테이블 $($tbls.Count)개"

foreach ($t in $tbls) { Exec "USE [$Keep]; ALTER TABLE [$($t[0])].[$($t[1])] NOCHECK CONSTRAINT ALL" | Out-Null }
$del = 0
foreach ($t in $tbls) {
    try { $n = Exec "USE [$Keep]; DELETE FROM [$($t[0])].[$($t[1])]" 1800; $del += $n }
    catch { Say "  !! $($t[0]).$($t[1]) 삭제 실패: $($_.Exception.Message.Split([char]10)[0])" }
}
Say "  삭제 $del 행"

# IDENTITY 리시드 — 이관 결과의 RECORD_ID 가 1부터 시작하도록(대조·재현에 유리)
$cmdI = $conn.CreateCommand()
$cmdI.CommandText = "SELECT s.name, t.name FROM [$Keep].sys.tables t JOIN [$Keep].sys.schemas s ON s.schema_id=t.schema_id WHERE EXISTS (SELECT 1 FROM [$Keep].sys.identity_columns ic WHERE ic.object_id=t.object_id)"
$ri = $cmdI.ExecuteReader()
$idt = @()
while ($ri.Read()) { $idt += ,@($ri[0], $ri[1]) }
$ri.Close()
foreach ($t in $idt) {
    try { Exec "USE [$Keep]; DBCC CHECKIDENT ('[$($t[0])].[$($t[1])]', RESEED, 0) WITH NO_INFOMSGS" | Out-Null } catch { }
}
Say "  IDENTITY 리시드 $($idt.Count)개 테이블"

foreach ($t in $tbls) {
    try { Exec "USE [$Keep]; ALTER TABLE [$($t[0])].[$($t[1])] WITH CHECK CHECK CONSTRAINT ALL" 600 | Out-Null }
    catch { Say "  !! $($t[0]).$($t[1]) 제약 재활성 실패: $($_.Exception.Message.Split([char]10)[0])" }
}
Say "  제약 재활성 완료"

$after = Scalar "SELECT ISNULL(SUM(p.rows),0) FROM [$Keep].sys.tables t JOIN [$Keep].sys.partitions p ON p.object_id=t.object_id AND p.index_id IN (0,1)"
Say "  이후 총 행수: $after"
if ([int]$after -ne 0) { Say "  ⚠ 0 이 아닙니다 — 남은 테이블을 확인하세요." }
Say ""

# ── 4) 개명 ────────────────────────────────────────────────────────────────
Say "── 개명 $Keep → $NewName ──"
if (DbExists $NewName) {
    Say "  !! '$NewName' 가 이미 있습니다 — 개명하지 않습니다."
} else {
    Exec "ALTER DATABASE [$Keep] SET SINGLE_USER WITH ROLLBACK IMMEDIATE" | Out-Null
    Exec "ALTER DATABASE [$Keep] MODIFY NAME = [$NewName]" | Out-Null
    Exec "ALTER DATABASE [$NewName] SET MULTI_USER" | Out-Null
    Say "  완료"
}
Say ""

# ── 5) 검증 ────────────────────────────────────────────────────────────────
Say "── 검증 ──"
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT name, state_desc FROM sys.databases WHERE database_id > 4 ORDER BY name"
$r = $cmd.ExecuteReader()
while ($r.Read()) { Say ("  {0,-24} {1}" -f $r[0], $r[1]) }
$r.Close()
Say ""
Say "백업 위치: $BackupDir"
Say "이제 도구의 타겟을 [$NewName] 로 바꾼 뒤 전량 재구축으로 이관하세요."
$conn.Close()
