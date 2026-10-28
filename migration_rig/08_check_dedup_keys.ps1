# ════════════════════════════════════════════════════════════════════════════
#  08_check_dedup_keys.ps1  —  이관 계획의 dedup 키가 실제로 고유한지 소스에서 검증
#
#  왜 필요한가 (2026-08-19 실측)
#    TR_FRAME 의 dedup 키를 MFB_NO 로 잡아 뒀는데, MFB_NO 는 순번이 아니라
#    "유효 MFB 밴드 개수" 라 495행 전부 값이 32 였다. 그 결과 495행이 3행으로 뭉개졌고,
#    게이트에는 오류도 스킵도 아닌 "재사용 492건" 이라는 정상적으로 보이는 숫자로 나왔다.
#    커밋했으면 순간소음 특징벡터 99% 가 조용히 사라졌을 것이다.
#
#    키가 고유하지 않으면 dedup 은 "중복 방지" 가 아니라 "데이터 삭제" 가 된다.
#    그리고 그 손실은 어떤 게이트에도 걸리지 않는다 — 그래서 소스에서 직접 세는 수밖에 없다.
#
#  하는 일
#    계획에 적힌 (부모FK + 키컬럼) 조합의 고유값 수를 소스 행수와 대조한다.
#    행수보다 조합 수가 적으면 그 차이가 곧 "이관하면 사라질 행" 이다.
#
#  읽기 전용 — 소스를 조회만 한다.
# ════════════════════════════════════════════════════════════════════════════
param(
    [string]$Dbq       = 'localhost:1522/NAIMS12',
    [string]$Driver    = 'Oracle in OraDB12Home1',
    [string]$Uid       = 'LEGACYNAIMS',
    [string]$Pwd       = '',
    [string]$ChecksJson = "$PSScriptRoot\_dedup_checks.json"
)

$ErrorActionPreference = 'Stop'
if (-not $Pwd) { $Pwd = if ($env:LEGACYNAIMS_PW) { $env:LEGACYNAIMS_PW } else { 'naims2!' } }

$checks = Get-Content $ChecksJson -Raw -Encoding UTF8 | ConvertFrom-Json
$conn = New-Object System.Data.Odbc.OdbcConnection("Driver={$Driver};DBQ=$Dbq;Uid=$Uid;Pwd=$Pwd;")
$conn.Open()
Write-Host "===== dedup 키 고유성 검증 =====  소스: $Dbq"
Write-Host ""

function Scalar($sql) {
    $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 120
    $v = $cmd.ExecuteScalar()
    if ($null -eq $v -or $v -is [DBNull]) { return 0 }
    return [int64]$v
}

Write-Host ("{0,-24} {1,-7} {2,7} {3,8} {4,8}  {5}" -f '소스테이블','방식','행수','고유조합','손실','판정 / 키')
Write-Host ('-' * 118)

$bad = @()
foreach ($c in $checks) {
    $tbl  = $c[0]; $kind = $c[1]; $cols = @($c[2])

    # 계획의 키가 매핑표에서 소스 컬럼을 못 찾은 경우 '?' 접두어로 표시돼 온다
    # -like '?*' 는 ? 가 한 글자 와일드카드라 모든 문자열이 매치된다 — 리터럴 비교로 본다
    $unmapped = $cols | Where-Object { $_.StartsWith('?') }
    if ($unmapped) {
        Write-Host ("{0,-24} {1,-7} {2,7} {3,8} {4,8}  ⚠ 소스 컬럼 미상: {5}" -f $tbl,$kind,'-','-','-',($unmapped -join ','))
        $bad += [pscustomobject]@{ Table=$tbl; Kind=$kind; Verdict='컬럼미상'; Rows=0; Combos=0; Loss=0; Keys=($cols -join ', ') }
        continue
    }

    $colList = ($cols | ForEach-Object { $_ }) -join ', '
    try {
        $rows   = Scalar "SELECT COUNT(*) FROM $tbl"
        # NULL 이 섞여도 조합으로 세어지도록 DISTINCT 서브쿼리를 쓴다(연결 문자열 방식은 NULL 에서 무너진다)
        $combos = Scalar "SELECT COUNT(*) FROM (SELECT DISTINCT $colList FROM $tbl)"
    }
    catch {
        Write-Host ("{0,-24} {1,-7} {2,7} {3,8} {4,8}  !! 조회 실패: {5}" -f $tbl,$kind,'-','-','-',$_.Exception.Message.Split([char]10)[0])
        continue
    }

    $loss = $rows - $combos
    $verdict = if ($rows -eq 0)      { '· 소스 비어있음' }
               elseif ($loss -eq 0)  { '✅ 고유' }
               elseif ($combos -le 1){ "❌ 사실상 상수 — 전량 붕괴" }
               else                  { "❌ 중복 — $loss 행 유실" }

    Write-Host ("{0,-24} {1,-7} {2,7} {3,8} {4,8}  {5}  ({6})" -f $tbl,$kind,$rows,$combos,$loss,$verdict,$colList)

    if ($loss -gt 0) {
        $bad += [pscustomobject]@{ Table=$tbl; Kind=$kind; Verdict=$verdict; Rows=$rows; Combos=$combos; Loss=$loss; Keys=$colList }
    }
}

Write-Host ""
if ($bad.Count -eq 0) {
    Write-Host "✅ 모든 dedup 키가 소스에서 고유합니다 — 붕괴로 인한 유실 없음."
} else {
    Write-Host "❌ 문제 있는 키 $($bad.Count) 건 — 이 테이블들은 dedup 을 끄거나 키를 바꿔야 합니다:"
    foreach ($b in $bad) {
        Write-Host ("   · {0,-22} {1,6}행 → {2,6}조합   유실 {3,6}행   [{4}]" -f $b.Table, $b.Rows, $b.Combos, $b.Loss, $b.Keys)
    }
    $out = Join-Path $PSScriptRoot "_dedup_key_problems.csv"
    $bad | Export-Csv -Path $out -NoTypeInformation -Encoding UTF8
    Write-Host ""
    Write-Host "상세: $out"
}
$conn.Close()
