# 99_verify.ps1  -  connect via LegacyNaims DSN, decrypt, verify data. ASCII/English.
try { chcp 65001 > $null } catch {}
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

. "$PSScriptRoot\_secrets.ps1"   # 비밀번호는 코드에 두지 않는다 (환경변수 → 파라미터 → 화면 입력)
$cs = "DSN=LegacyNaims;Uid=LEGACYNAIMS;Pwd=$(Get-NaimsSecret -EnvName 'LEGACYNAIMS_PW' -Prompt 'LEGACYNAIMS 스키마 비밀번호' -DefaultValue 'naims2!');"
$c = New-Object System.Data.Odbc.OdbcConnection($cs)
try {
  $c.Open(); $cmd = $c.CreateCommand()
  $cmd.CommandText = "SELECT COUNT(*) FROM user_tables"
  Write-Host ("table count     = " + $cmd.ExecuteScalar())
  $cmd.CommandText = "SELECT COUNT(*) FROM classes"
  Write-Host ("CLASSES rows    = " + $cmd.ExecuteScalar())
  $cmd.CommandText = "SELECT class_id, CRYPTO.DECRYPT(UTL_RAW.CAST_TO_VARCHAR2(class_name)) FROM classes WHERE ROWNUM<=3"
  $r = $cmd.ExecuteReader()
  while ($r.Read()) { Write-Host ("  class {0} decrypt -> '{1}'" -f $r.GetValue(0), $r.GetString(1)) }
  $r.Close(); $c.Close()
  Write-Host "=> OK: connection, data and CRYPTO.DECRYPT all working." -ForegroundColor Green
} catch {
  Write-Host ("VERIFY FAILED: " + $_.Exception.Message.Split([char]10)[0]) -ForegroundColor Red
  Write-Host "  Try: 98_diagnose.ps1 (overall), 05b_fix_dsn.ps1 (DSN), 97_fix_user_and_crypto.sql (user/crypto)."
  if ($c.State -ne 'Closed') { $c.Close() }
}
