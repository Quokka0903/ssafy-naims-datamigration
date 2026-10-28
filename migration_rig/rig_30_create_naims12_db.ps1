# ============================================================================
# rig_30_create_naims12_db.ps1
# 12.2 소프트웨어 온리 설치가 성공한 뒤 실행.
# - 21c(1521)와 공존하도록 별도 리스너 LISTENER_N12(포트 1522) 생성/기동
# - NAIMS12 DB 생성 (non-CDB=레거시 10gR2 재현, 문자셋 KO16MSWIN949=필드 재현)
# 접속(생성 후): system/<비밀번호>@localhost:1522/NAIMS12  ($env:NAIMS12_SYS_PW)
# ============================================================================
. "$PSScriptRoot\_secrets.ps1"   # 비밀번호는 코드에 두지 않는다 (환경변수 → 파라미터 → 화면 입력)
$SysPw = Get-NaimsSecret -EnvName 'NAIMS12_SYS_PW' -Prompt 'NAIMS12 SYSTEM 비밀번호' -DefaultValue 'Naims12#2026'
$OH = "D:\app\oracle\product\12.2.0\dbhome_1"
if (-not (Test-Path "$OH\bin\dbca.bat")) { Write-Error "12.2 홈이 없음: $OH — 소프트웨어 설치 먼저"; exit 1 }
$env:ORACLE_HOME = $OH
$env:ORACLE_SID  = "NAIMS12"
$env:PATH        = "$OH\bin;$env:PATH"

# 1) 별도 리스너(1522) 구성 + 기동 -------------------------------------------
$netAdmin = "$OH\network\admin"
New-Item -ItemType Directory -Force -Path $netAdmin | Out-Null
@"
LISTENER_N12 =
  (DESCRIPTION_LIST =
    (DESCRIPTION =
      (ADDRESS = (PROTOCOL = TCP)(HOST = 0.0.0.0)(PORT = 1522))
    )
  )
"@ | Set-Content "$netAdmin\listener.ora" -Encoding ASCII
& "$OH\bin\lsnrctl.exe" start LISTENER_N12

# 2) NAIMS12 생성 (non-CDB, KO16MSWIN949) -------------------------------------
& "$OH\bin\dbca.bat" -silent -createDatabase `
  -templateName General_Purpose.dbc `
  -gdbName NAIMS12 -sid NAIMS12 `
  -createAsContainerDatabase false `
  -characterSet KO16MSWIN949 -nationalCharacterSet AL16UTF16 `
  -sysPassword "$SysPw" -systemPassword "$SysPw" `
  -memoryMgmtType AUTO_SGA -totalMemory 2560 `
  -emConfiguration NONE `
  -listeners LISTENER_N12 `
  -storageType FS -datafileDestination "D:\app\oracle\oradata" `
  -ignorePreReqs
Write-Output "=== dbca 종료 코드: $LASTEXITCODE ==="
# 확인: sqlplus system/<비밀번호>@localhost:1522/NAIMS12 -> select * from v$version;
