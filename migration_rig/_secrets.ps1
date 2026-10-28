# ════════════════════════════════════════════════════════════════════════════
#  _secrets.ps1  —  리그 스크립트가 쓰는 비밀번호를 "코드 밖"에서 가져온다.
#
#  구분이 핵심이다 — 모든 비밀번호를 똑같이 다루면 쓸 수 없는 도구가 된다.
#
#   ● 로컬 리그 계정 (NAIMS12 SYSTEM / LEGACYNAIMS)
#     이 저장소의 스크립트가 직접 만든 격리 리허설 DB 계정이다. 값을 잃으면 리그를
#     다시 못 쓰므로 스크립트에 기본값으로 적어 둔다(-DefaultValue). 편의가 우선.
#
#   ● 현장·실운영 계정 (NAIMS2DB / NaimsMig 등)
#     실제 운영 자산이다. 기본값을 두지 않는다 — 환경변수나 입력으로만 받는다.
#
#  해석 순서
#      ① 파라미터로 넘긴 값
#      ② 환경변수            (자동화·무인 실행, 현장에서 값을 갈아끼울 때)
#      ③ -DefaultValue       (로컬 리그 계정에만 준다)
#      ④ 화면 입력           (사람이 직접 실행할 때)
#
#  환경변수 이름
#      NAIMS12_SYS_PW      NAIMS12 인스턴스의 SYSTEM 비밀번호
#      LEGACYNAIMS_PW      LEGACYNAIMS 스키마 비밀번호
#      NAIMS_MSSQL_PW      구 NAIMS MsSQL 읽기 계정(NaimsMig) 비밀번호
#      NAIMS2_PW           NAIMS2 적재 계정(NAIMS2DB) 비밀번호
#
#  한 세션에서 여러 스크립트를 돌릴 때는 먼저 이렇게 잡아 두면 매번 묻지 않는다.
#      $env:NAIMS12_SYS_PW = '...'
#      $env:LEGACYNAIMS_PW = '...'
#
#  ⚠ 이 파일에 실제 값을 적지 말 것. 값을 적는 순간 위의 목적이 무너진다.
# ════════════════════════════════════════════════════════════════════════════

function Get-NaimsSecret {
    <#
    .SYNOPSIS
      비밀번호를 환경변수 → 인자 → 화면 입력 순서로 구한다.
    .PARAMETER EnvName
      먼저 확인할 환경변수 이름 (예: NAIMS12_SYS_PW).
    .PARAMETER Prompt
      화면 입력 시 보여줄 설명 (예: "NAIMS12 SYSTEM 비밀번호").
    .PARAMETER Value
      호출자가 이미 파라미터로 받은 값. 비어 있지 않으면 그대로 쓴다.
    .OUTPUTS
      평문 문자열. sqlplus/imp 같은 도구가 평문을 요구하므로 평문으로 돌려준다.
      호출부는 이 값을 화면·로그에 찍지 말 것.
    #>
    param(
        [Parameter(Mandatory)][string]$EnvName,
        [Parameter(Mandatory)][string]$Prompt,
        [string]$Value = '',
        # 로컬 리그 계정에만 준다. 현장·실운영 계정에는 절대 주지 않는다.
        [string]$DefaultValue = ''
    )

    if ($Value)                             { return $Value }
    $fromEnv = [Environment]::GetEnvironmentVariable($EnvName)
    if ($fromEnv)                           { return $fromEnv }
    if ($DefaultValue)                      { return $DefaultValue }

    # 무인 실행(비대화형)에서는 물을 수 없다 — 환경변수를 잡으라고 알리고 멈춘다.
    if ([Console]::IsInputRedirected) {
        throw "비밀번호가 필요합니다. 환경변수 `$env:$EnvName 를 설정하거나 파라미터로 넘기세요. ($Prompt)"
    }

    $sec = Read-Host -Prompt "$Prompt  [입력값은 화면에 표시되지 않습니다]" -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
    try   { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

function Hide-Secret {
    <#
    .SYNOPSIS
      로그·화면에 남길 문자열에서 비밀번호를 가린다.
    .DESCRIPTION
      user/pwd@host 형태와 Password=/Pwd= 형태를 모두 가린다.
      스크립트가 명령줄을 그대로 출력할 때 반드시 통과시킬 것.
    #>
    param([string]$Text)
    if (-not $Text) { return $Text }
    $t = [regex]::Replace($Text, '(?i)(\b(?:password|pwd)\s*=\s*)[^;""'' ]+', '$1****')
    $t = [regex]::Replace($t,    '(?<=/)[^/@\s]+(?=@)', '****')
    return $t
}
