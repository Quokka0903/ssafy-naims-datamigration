<#
════════════════════════════════════════════════════════════════════════════════
 15_seed_snippets_from_share.ps1
   공유 폴더 SNIPPET 에 <있는> 스니펫 파일을, 레거시 Oracle 테스트 DB 에 레코드로 넣는다.

 왜 필요한가 (2026-08-19 실측)
   공유 폴더와 DB 가 서로 다른 것을 가리키고 있었다.
     · SNIPPET 폴더 : nb 24쌍 · ai grm 7/wav 5 · tr wav 3
     · 레거시 DB    : NB 22행(그중 6행은 nb_CDMU_* — 폴더에 파일 없음)
                      AI 3행(ai_65407061 만 wav 존재, grm 없음)
                      TR 4행(폴더의 tr_* 3개와 <한 건도> 겹치지 않음)
   그래서 이관을 돌려도 스니펫 blob 이 죄다 빈 값이었다. 이관기가 틀린 게 아니라
   대조할 자료가 없었던 것이다. 검증하려면 "파일이 실제로 있는 레코드"가 있어야 한다.

 무엇을 하나
   폴더에 있는데 DB 에 없는 파일마다 레코드 한 건을 만든다.
   내용은 <기존 레코드를 그대로 복사>하고 — INSERT ... SELECT 로 통째로 뜬다 —
   다음만 현실에 맞춘다:
     · PK        : 새 번호 (100000000 대역, 기존 최대값 다음부터)
     · FK        : 실재하는 부모에 돌아가며 붙임 (NB→SIGNATURES, AI→PULSES, TR→TR_FRAME)
     · FILENAME  : 실제 파일 이름
     · 그램 제원 : NO_FRAME / FREQ_MIN / FREQ_MAX / RESOLUTION 은 .grm 헤더에서 읽는다
     · SAMPLING_RATE : .wav 의 fmt 청크에서 읽는다

   ⚠ 그램 제원까지 읽는 이유 — 이 값들은 "설명"이 아니라 blob 을 <해석하는 규격>이다.
     복사본 값을 그대로 두면 실제 파일과 어긋나 그램이 깨진 것처럼 보인다. 그러면
     이 자료로는 아무것도 검증할 수 없다. 시각·좌표·함정 같은 서술 값은 복사한다.

   ⚠ 짝이 없는 파일(예: ai_53137632.grm 은 wav 가 없음)은 <없는 쪽 이름을 비워 둔다>.
     그럴듯한 이름을 지어 넣으면 나중에 "파일 없음"으로 다시 튀어나온다. 빈 값이 사실이다.

 대상 확인
   기본 DSN 은 LegacyNaims = localhost:1522/LegacyNaims — 이 저장소가 만든 격리 리허설 DB다.
   원격/현장 DB 를 건드리려면 -AllowRemote 와 -ConfirmTarget 을 <직접 타이핑>해야 한다.

 사용
   .\15_seed_snippets_from_share.ps1                 # 계획만 보여줌 (아무것도 쓰지 않음)
   .\15_seed_snippets_from_share.ps1 -Apply          # 실제 삽입 (트랜잭션)
════════════════════════════════════════════════════════════════════════════════
#>
[CmdletBinding()]
param(
    [string] $SnippetDir = '\\192.168.111.165\naims_share\MSU\CLASSIFICATIONDB\SNIPPET',
    [string] $Dsn        = 'LegacyNaims',
    [string] $User       = 'LEGACYNAIMS',
    [string] $Password,
    [switch] $Apply,
    [switch] $AllowRemote,
    [string] $ConfirmTarget
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_secrets.ps1')

if (-not $Password) {
    $Password = Get-NaimsSecret -EnvName 'LEGACYNAIMS_PW' -Prompt 'LEGACYNAIMS 스키마 비밀번호' -DefaultValue 'naims2!'
}

# ── 대상 확인 ────────────────────────────────────────────────────────────────
# 이 스크립트는 자료를 <만든다>. 만드는 것도 잘못된 DB 에 하면 되돌리기 어렵다.
$dsnKey = @('HKCU:\SOFTWARE\ODBC\ODBC.INI', 'HKLM:\SOFTWARE\ODBC\ODBC.INI') |
          ForEach-Object { Join-Path $_ $Dsn } | Where-Object { Test-Path $_ } | Select-Object -First 1
$dbq = if ($dsnKey) { (Get-ItemProperty $dsnKey).DBQ } else { '(DSN 정보 없음)' }
Write-Host "대상 DSN : $Dsn  →  $dbq" -ForegroundColor Cyan

$isLocal = $dbq -match '(?i)localhost|127\.0\.0\.1|\(HOST\s*=\s*localhost'
if (-not $isLocal) {
    if (-not $AllowRemote) { throw "원격으로 보이는 대상입니다 ($dbq). 정말 맞다면 -AllowRemote 와 -ConfirmTarget 을 주세요." }
    if ($ConfirmTarget -ne $dbq) { throw "-ConfirmTarget 에 대상 문자열을 그대로 입력하세요: $dbq" }
}

# ══════════════════════════════════════════════════════════════════════════════
#  파일 읽기 — 레거시 규격
# ══════════════════════════════════════════════════════════════════════════════
# 헤더 배치는 RawCollectMigrator/Features/DbToDb/Services/LegacyGrmReader.cs 와 같아야 한다.
#   NB(128B) @8 int32 StartTime · @12 EndTime · @16 float StartFreq · @20 EndFreq
#            @24 float Resolution · @28 int32 FrameNum · @32 int32 BinWidth
#   AI(216B) @0 int64 StartTime · @8 int64 EndTime · @16 float StartFreq · @20 EndFreq
#            @24 int32 BinWidth · @28 int32 FrameNum · @32 float Resolution
function Read-GrmHeader {
    param([string]$Path, [ValidateSet('nb','ai')][string]$Kind)
    $b = [IO.File]::ReadAllBytes($Path)
    $need = if ($Kind -eq 'nb') { 128 } else { 216 }
    if ($b.Length -lt $need) { return $null }
    if ($Kind -eq 'nb') {
        [pscustomobject]@{
            StartFreq = [BitConverter]::ToSingle($b, 16)
            EndFreq   = [BitConverter]::ToSingle($b, 20)
            Resolution= [BitConverter]::ToSingle($b, 24)
            FrameNum  = [BitConverter]::ToInt32($b, 28)
            BinWidth  = [BitConverter]::ToInt32($b, 32)
        }
    } else {
        [pscustomobject]@{
            StartFreq = [BitConverter]::ToSingle($b, 16)
            EndFreq   = [BitConverter]::ToSingle($b, 20)
            BinWidth  = [BitConverter]::ToInt32($b, 24)
            FrameNum  = [BitConverter]::ToInt32($b, 28)
            Resolution= [BitConverter]::ToSingle($b, 32)
        }
    }
}

# BWF(방송용 wav)라 bext/NDID 같은 청크가 앞에 붙는다. 고정 오프셋으로 읽으면 틀린다 — 청크를 순회한다.
function Read-WaveFmt {
    param([string]$Path)
    $b = [IO.File]::ReadAllBytes($Path)
    if ($b.Length -lt 12) { return $null }
    if ([Text.Encoding]::ASCII.GetString($b, 0, 4) -ne 'RIFF') { return $null }
    $off = 12; $rate = 0; $bits = 0; $ch = 0; $dataLen = 0
    while ($off + 8 -le $b.Length) {
        $id  = [Text.Encoding]::ASCII.GetString($b, $off, 4)
        $len = [BitConverter]::ToInt32($b, $off + 4)
        if ($len -lt 0) { break }
        $body = $off + 8
        if ($id -eq 'fmt ' -and $body + 16 -le $b.Length) {
            $ch   = [BitConverter]::ToInt16($b, $body + 2)
            $rate = [BitConverter]::ToInt32($b, $body + 4)
            $bits = [BitConverter]::ToInt16($b, $body + 14)
        } elseif ($id -eq 'data') {
            $dataLen = [Math]::Min($len, $b.Length - $body)
        }
        $off = $body + $len + ($len % 2)   # 청크는 짝수 정렬
    }
    if ($rate -le 0) { return $null }
    [pscustomobject]@{ Rate = $rate; Bits = $bits; Channels = $ch; DataBytes = $dataLen }
}

# ══════════════════════════════════════════════════════════════════════════════
#  DB
# ══════════════════════════════════════════════════════════════════════════════
$cn = New-Object System.Data.Odbc.OdbcConnection("DSN=$Dsn;Uid=$User;Pwd=$Password;")
$cn.Open()
Write-Host "접속 OK ($User@$Dsn)" -ForegroundColor Green

function Query([string]$sql) {
    $a = New-Object System.Data.Odbc.OdbcDataAdapter($sql, $cn)
    $d = New-Object System.Data.DataTable
    [void]$a.Fill($d)
    ,$d
}
function Scalar([string]$sql) {
    $c = $cn.CreateCommand(); $c.CommandText = $sql
    $v = $c.ExecuteScalar(); $c.Dispose(); $v
}
function Col([string]$sql) { (Query $sql).Rows | ForEach-Object { $_[0] } }

# ── 현재 상태 ────────────────────────────────────────────────────────────────
$nbHave = @{}; Col "SELECT TRIM(GRAM_FILENAME) FROM NB_SNIPPET"  | ForEach-Object { if ($_ -is [string] -and $_) { $nbHave[[IO.Path]::GetFileNameWithoutExtension($_)] = $true } }
$aiHave = @{}; Col "SELECT TRIM(GRAM_FILENAME) FROM AI_SNIPPET"  | ForEach-Object { if ($_ -is [string] -and $_) { $aiHave[[IO.Path]::GetFileNameWithoutExtension($_)] = $true } }
Col "SELECT TRIM(AUDIO_FILENAME) FROM AI_SNIPPET" | ForEach-Object { if ($_ -is [string] -and $_) { $aiHave[[IO.Path]::GetFileNameWithoutExtension($_)] = $true } }
$trHave = @{}; Col "SELECT TRIM(AUDIO_FILENAME) FROM TR_SNIPPET" | ForEach-Object { if ($_ -is [string] -and $_) { $trHave[[IO.Path]::GetFileNameWithoutExtension($_)] = $true } }

# ── 템플릿(복사 원본)과 부모 후보 ────────────────────────────────────────────
$tplNb = Scalar "SELECT MIN(NB_SNIPPET_ID) FROM NB_SNIPPET"
$tplAi = Scalar "SELECT MIN(AI_SNIPPET_ID) FROM AI_SNIPPET"
$tplTr = Scalar "SELECT MIN(TR_SNIPPET_ID) FROM TR_SNIPPET"
$tplSig = Scalar "SELECT MIN(SIG_SNP_ID) FROM SIG_SNIPPET"
$tplFrm = Scalar "SELECT MIN(FRM_SNP_ID) FROM FRAME_SNIPPET"
foreach ($p in @(@('NB_SNIPPET',$tplNb), @('AI_SNIPPET',$tplAi), @('TR_SNIPPET',$tplTr))) {
    if ($null -eq $p[1] -or $p[1] -is [DBNull]) { throw "$($p[0]) 에 복사할 기존 레코드가 없습니다 — 이 스크립트는 기존 행을 본떠 만듭니다." }
}

$sigIds   = @(Col "SELECT SIGNATURE_ID FROM SIGNATURES WHERE NVL(DELETED,0)=0 ORDER BY SIGNATURE_ID")
$pulseIds = @(Col "SELECT PULSE_ID     FROM PULSES     WHERE NVL(DELETED,0)=0 ORDER BY PULSE_ID")
$frameIds = @(Col "SELECT FRAME_ID     FROM TR_FRAME   WHERE NVL(DELETED,0)=0 ORDER BY FRAME_ID")

function NextId([string]$table, [string]$pk) {
    $m = Scalar "SELECT NVL(MAX($pk),0) FROM $table"
    [long]([Math]::Max([double]$m, 100000000)) + 1
}
$nextNb = NextId 'NB_SNIPPET' 'NB_SNIPPET_ID'
$nextAi = NextId 'AI_SNIPPET' 'AI_SNIPPET_ID'
$nextTr = NextId 'TR_SNIPPET' 'TR_SNIPPET_ID'
$nextSig= NextId 'SIG_SNIPPET' 'SIG_SNP_ID'
$nextFrm= NextId 'FRAME_SNIPPET' 'FRM_SNP_ID'

# ══════════════════════════════════════════════════════════════════════════════
#  계획 세우기
# ══════════════════════════════════════════════════════════════════════════════
if (-not (Test-Path $SnippetDir)) { throw "스니펫 폴더에 접근할 수 없습니다: $SnippetDir" }
$files = Get-ChildItem $SnippetDir -File

function Stems([string]$prefix) {
    $files | Where-Object { $_.Name -like "$prefix*" } |
        Group-Object { [IO.Path]::GetFileNameWithoutExtension($_.Name) } |
        ForEach-Object {
            [pscustomobject]@{
                Stem = $_.Name
                Grm  = ($_.Group | Where-Object Extension -eq '.grm' | Select-Object -First 1)
                Wav  = ($_.Group | Where-Object Extension -eq '.wav' | Select-Object -First 1)
            }
        } | Sort-Object Stem
}

$plan = New-Object System.Collections.Generic.List[object]
$skipped = New-Object System.Collections.Generic.List[object]

# ── NB : GRAM_FILENAME 기준. 그램이 없으면 넣지 않는다(협대역 스니펫의 본체는 그램이다). ──
$i = 0
foreach ($s in (Stems 'nb_')) {
    if ($nbHave.ContainsKey($s.Stem)) { continue }
    if (-not $s.Grm) { $skipped.Add([pscustomobject]@{ 표='NB'; 이름=$s.Stem; 사유='.grm 없음' }); continue }
    $h = Read-GrmHeader $s.Grm.FullName 'nb'
    if (-not $h) { $skipped.Add([pscustomobject]@{ 표='NB'; 이름=$s.Stem; 사유='.grm 헤더 짧음' }); continue }
    $w = if ($s.Wav) { Read-WaveFmt $s.Wav.FullName } else { $null }
    $plan.Add([pscustomobject]@{
        표='NB'; PK=$nextNb; 이름=$s.Stem
        그램=$s.Grm.Name; 음원=$(if($s.Wav){$s.Wav.Name}else{''})
        부모="SIGNATURE_ID=$($sigIds[$i % [Math]::Max(1,$sigIds.Count)])"
        프레임=$h.FrameNum; 빈=$h.BinWidth
        주파수="$([Math]::Round($h.StartFreq,3))~$([Math]::Round($h.EndFreq,3))"
        해상도=[Math]::Round($h.Resolution,4)
        표본율=$(if($w){$w.Rate}else{$null})
        _h=$h; _w=$w; _sig=$sigIds[$i % [Math]::Max(1,$sigIds.Count)]
        _grm=$s.Grm.Name; _wav=$(if($s.Wav){$s.Wav.Name}else{''})
    })
    $nextNb++; $i++
}

# ── AI : 그램 또는 음원 중 하나만 있어도 넣는다. 없는 쪽 이름은 비워 둔다. ──
$i = 0
foreach ($s in (Stems 'ai_')) {
    if ($aiHave.ContainsKey($s.Stem)) { continue }
    $h = if ($s.Grm) { Read-GrmHeader $s.Grm.FullName 'ai' } else { $null }
    $w = if ($s.Wav) { Read-WaveFmt $s.Wav.FullName } else { $null }
    if (-not $s.Grm -and -not $s.Wav) { continue }
    $plan.Add([pscustomobject]@{
        표='AI'; PK=$nextAi; 이름=$s.Stem
        그램=$(if($s.Grm){$s.Grm.Name}else{''}); 음원=$(if($s.Wav){$s.Wav.Name}else{''})
        부모="PULSE_ID=$($pulseIds[$i % [Math]::Max(1,$pulseIds.Count)])"
        프레임=$(if($h){$h.FrameNum}else{$null}); 빈=$(if($h){$h.BinWidth}else{$null})
        주파수=$(if($h){"$([Math]::Round($h.StartFreq,3))~$([Math]::Round($h.EndFreq,3))"}else{'(복사)'})
        해상도=$(if($h){[Math]::Round($h.Resolution,4)}else{$null})
        표본율=$(if($w){$w.Rate}else{$null})
        _h=$h; _w=$w; _pulse=$pulseIds[$i % [Math]::Max(1,$pulseIds.Count)]
        _grm=$(if($s.Grm){$s.Grm.Name}else{''}); _wav=$(if($s.Wav){$s.Wav.Name}else{''})
    })
    $nextAi++; $i++
}

# ── TR : 음원만 있다(스펙트로그램은 신규 DB 에서 원음으로 만든다). ──
$i = 0
foreach ($s in (Stems 'tr_')) {
    if ($trHave.ContainsKey($s.Stem)) { continue }
    if (-not $s.Wav) { $skipped.Add([pscustomobject]@{ 표='TR'; 이름=$s.Stem; 사유='.wav 없음' }); continue }
    $w = Read-WaveFmt $s.Wav.FullName
    $plan.Add([pscustomobject]@{
        표='TR'; PK=$nextTr; 이름=$s.Stem
        그램=''; 음원=$s.Wav.Name
        부모="FRAME_ID=$($frameIds[$i % [Math]::Max(1,$frameIds.Count)])"
        프레임=$null; 빈=$null; 주파수='(복사)'; 해상도=$null
        표본율=$(if($w){$w.Rate}else{$null})
        _h=$null; _w=$w; _frame=$frameIds[$i % [Math]::Max(1,$frameIds.Count)]
        _grm=''; _wav=$s.Wav.Name
    })
    $nextTr++; $i++
}

# ── 계획 보여주기 ────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "── 삽입 계획 ($($plan.Count)건) ──" -ForegroundColor Yellow
$plan | Format-Table 표,PK,이름,그램,음원,부모,프레임,빈,주파수,해상도,표본율 -AutoSize
if ($skipped.Count -gt 0) {
    Write-Host "── 건너뜀 ($($skipped.Count)건) ──" -ForegroundColor DarkYellow
    $skipped | Format-Table -AutoSize
}

# 폴더에 파일이 없는 <기존> 레코드도 알려 준다. 고치지는 않는다 — 사람이 판단할 일이다.
$stemsOnDisk = @{}; $files | ForEach-Object { $stemsOnDisk[[IO.Path]::GetFileNameWithoutExtension($_.Name)] = $true }
$orphanRows = New-Object System.Collections.Generic.List[object]
foreach ($q in @(
    @{T='NB'; S="SELECT NB_SNIPPET_ID, TRIM(GRAM_FILENAME) F FROM NB_SNIPPET"},
    @{T='AI'; S="SELECT AI_SNIPPET_ID, TRIM(GRAM_FILENAME) F FROM AI_SNIPPET"},
    @{T='TR'; S="SELECT TR_SNIPPET_ID, TRIM(AUDIO_FILENAME) F FROM TR_SNIPPET"})) {
    foreach ($r in (Query $q.S).Rows) {
        $n = "$($r['F'])"
        if (-not $n) { continue }
        $st = [IO.Path]::GetFileNameWithoutExtension($n)
        if (-not $stemsOnDisk.ContainsKey($st)) { $orphanRows.Add([pscustomobject]@{ 표=$q.T; PK=$r[0]; 파일=$n }) }
    }
}
if ($orphanRows.Count -gt 0) {
    Write-Host "── ⚠ 파일이 없는 기존 레코드 ($($orphanRows.Count)건) — 이관하면 빈 blob 이 됩니다 ──" -ForegroundColor DarkYellow
    $orphanRows | Format-Table -AutoSize
}

if (-not $Apply) {
    Write-Host ""
    Write-Host "계획만 보여줬습니다. 실제로 넣으려면 -Apply 를 붙여 다시 실행하세요." -ForegroundColor Cyan
    $cn.Close(); return
}
if ($plan.Count -eq 0) { Write-Host "넣을 것이 없습니다." -ForegroundColor Green; $cn.Close(); return }

# ══════════════════════════════════════════════════════════════════════════════
#  삽입 — INSERT ... SELECT 로 템플릿 행을 통째로 복사하고 몇 칸만 덮어쓴다
# ══════════════════════════════════════════════════════════════════════════════
$tx = $cn.BeginTransaction()
try {
    # $args 라는 이름은 쓰지 않는다 — PowerShell 자동 변수와 겹쳐 인자가 조용히 안 실린다
    # (실측: ORA-01008 '일부 변수가 바인드되지 않았습니다').
    # 값 형식도 명시한다. 형식 없는 DBNull 은 Oracle ODBC 가 무엇으로 보낼지 모른다.
    # $args 라는 이름은 쓰지 않는다 — PowerShell 자동 변수와 겹쳐 인자가 조용히 안 실린다
    # (실측: ORA-01008 "일부 변수가 바인드되지 않았습니다").
    #
    # 형식은 지정하지 않는다. Oracle ODBC 드라이버가 스스로 맞춘다 —
    # OdbcType 을 명시했더니 오히려 <빈 메시지 예외>가 났다(실측). 값만 넘긴다.
    #
    # 정수는 [decimal] 로 넘긴다. [long](Int64) 를 넘기면 이 드라이버가
    # <메시지 없는 예외>를 던진다(실측 2026-08-19) — SQL_C_SBIGINT 미지원으로 보인다.
    # Oracle NUMBER 에는 decimal 이 정확히 실린다(부동소수 반올림도 없다).
    #
    # NULL 도 넘기지 않는다. "값이 없으면 원본 컬럼을 그대로 쓴다"는 뜻이므로,
    # NVL(?, COL) 로 NULL 을 바인드하는 대신 <SQL 에서 ? 를 COL 로 바꿔> 아예 인자를 뺀다.
    # 형식 없는 NULL 바인드는 드라이버마다 다르게 터진다 — 안 보내는 것이 가장 확실하다.
    function Exec([string]$sql, [object[]]$binds) {
        $c = $cn.CreateCommand(); $c.Transaction = $tx; $c.CommandText = $sql
        foreach ($a in $binds) {
            $prm = $c.CreateParameter(); $prm.Value = $a; [void]$c.Parameters.Add($prm)
        }
        $n = $c.ExecuteNonQuery(); $c.Dispose(); $n
    }

    # 값이 있으면 '?' 와 인자 한 칸, 없으면 원본 컬럼 이름.
    function Slot([object]$value, [string]$fallbackColumn, [System.Collections.Generic.List[object]]$binds) {
        if ($null -eq $value) { return $fallbackColumn }
        $binds.Add($value); return '?'
    }

    $nNb = 0; $nAi = 0; $nTr = 0; $nLink = 0
    foreach ($p in $plan) {
        switch ($p.표) {
        'NB' {
            # 컬럼 순서는 USER_TAB_COLUMNS(COLUMN_ID) 순서와 같아야 한다.
            $b = New-Object System.Collections.Generic.List[object]
            $b.Add([decimal]$p.PK); $b.Add([string]$p._grm); $b.Add([string]$p._wav)
            $b.Add([double]$p._h.StartFreq); $b.Add([double]$p._h.EndFreq)
            $b.Add([decimal]$p._h.FrameNum); $b.Add([double]$p._h.Resolution)
            $sr = Slot $(if ($p._w) { [double]$p._w.Rate } else { $null }) 'SAMPLING_RATE' $b
            $b.Add([decimal]$tplNb)
            Exec @"
INSERT INTO NB_SNIPPET
SELECT ?, ?, ?, ?, ?, ?, ?, A_START_TIME, A_END_TIME, $sr,
       DET_SONAR, LATITUDE_DEGREE, LATITUDE_MINUTE, LATITUDE_SECOND,
       LONGITUDE_DEGREE, LONGITUDE_MINUTE, LONGITUDE_SECOND, DET_DATE,
       DET_PLATFORM, DISTANCE_KM, COMMENTS1, COMMENTS2, DATE_SET_CHNG, DELETED, SELF_DB
  FROM NB_SNIPPET WHERE NB_SNIPPET_ID = ?
"@ $b.ToArray() | Out-Null
            $nNb++
            # 협대역 스니펫은 SIG_SNIPPET 링크로 시그니처에 매달린다.
            Exec @"
INSERT INTO SIG_SNIPPET
SELECT ?, ?, ?, DATE_SET_CHNG, DELETED, SELF_DB FROM SIG_SNIPPET WHERE SIG_SNP_ID = ?
"@ @([decimal]$nextSig, [decimal]$p.PK, [decimal]$p._sig, [decimal]$tplSig) | Out-Null
            $nextSig++; $nLink++
        }
        'AI' {
            $h = $p._h
            $b = New-Object System.Collections.Generic.List[object]
            $b.Add([decimal]$p.PK); $b.Add([decimal]$p._pulse)
            $b.Add([string]$p._grm); $b.Add([string]$p._wav)
            $c1 = Slot $(if ($h) { [double]$h.StartFreq }  else { $null }) 'FREQ_MIN'      $b
            $c2 = Slot $(if ($h) { [double]$h.EndFreq }    else { $null }) 'FREQ_MAX'      $b
            $c3 = Slot $(if ($h) { [decimal]$h.FrameNum }     else { $null }) 'NO_FRAME'      $b
            $c4 = Slot $(if ($h) { [double]$h.Resolution } else { $null }) 'RESOLUTION'    $b
            $c5 = Slot $(if ($p._w) { [double]$p._w.Rate } else { $null }) 'SAMPLING_RATE' $b
            $b.Add([decimal]$tplAi)
            Exec @"
INSERT INTO AI_SNIPPET
SELECT ?, ?, ?, ?,
       $c1, $c2, $c3, $c4,
       A_ORG_CFREQ, A_START_TIME, A_END_TIME, $c5,
       DET_SONAR, LATITUDE_DEGREE, LATITUDE_MINUTE, LATITUDE_SECOND,
       LONGITUDE_DEGREE, LONGITUDE_MINUTE, LONGITUDE_SECOND, DET_DATE,
       DET_PLATFORM, DISTANCE_KM, COMMENTS1, COMMENTS2, DATE_SET_CHNG, DELETED, SELF_DB
  FROM AI_SNIPPET WHERE AI_SNIPPET_ID = ?
"@ $b.ToArray() | Out-Null
            $nAi++
        }
        'TR' {
            $b = New-Object System.Collections.Generic.List[object]
            $b.Add([decimal]$p.PK); $b.Add([string]$p._wav)
            $sr = Slot $(if ($p._w) { [double]$p._w.Rate } else { $null }) 'SAMPLING_RATE' $b
            $b.Add([decimal]$tplTr)
            Exec @"
INSERT INTO TR_SNIPPET
SELECT ?, ?, NO_FRAME, A_START_TIME, A_END_TIME, $sr,
       DET_SONAR, LATITUDE_DEGREE, LATITUDE_MINUTE, LATITUDE_SECOND,
       LONGITUDE_DEGREE, LONGITUDE_MINUTE, LONGITUDE_SECOND, DET_DATE,
       DET_PLATFORM, DISTANCE_KM, COMMENTS, DATE_SET_CHNG, DELETED, SELF_DB
  FROM TR_SNIPPET WHERE TR_SNIPPET_ID = ?
"@ $b.ToArray() | Out-Null
            $nTr++
            if ($null -ne $tplFrm -and $tplFrm -isnot [DBNull] -and $frameIds.Count -gt 0) {
                Exec @"
INSERT INTO FRAME_SNIPPET
SELECT ?, ?, ?, DATE_SET_CHNG, DELETED, SELF_DB FROM FRAME_SNIPPET WHERE FRM_SNP_ID = ?
"@ @([decimal]$nextFrm, [decimal]$p._frame, [decimal]$p.PK, [decimal]$tplFrm) | Out-Null
                $nextFrm++; $nLink++
            }
        }
        }
    }

    $tx.Commit()
    Write-Host ""
    Write-Host "커밋 완료 — NB $nNb · AI $nAi · TR $nTr · 링크 $nLink" -ForegroundColor Green
}
catch {
    $tx.Rollback()
    Write-Host "실패 — 전부 되돌렸습니다: $($_.Exception.Message)" -ForegroundColor Red
    throw
}
finally { $cn.Close() }

Write-Host ""
Write-Host "다음: 이관기에서 [전량 재구축] 으로 다시 돌려 스니펫 blob 이 채워지는지 확인하세요." -ForegroundColor Cyan
