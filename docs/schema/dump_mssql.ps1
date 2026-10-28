# 비밀번호는 스크립트에 두지 않는다(이 저장소는 git 추적 대상).
#   $env:NAIMS_MSSQL_PW 로 넘기거나, 없으면 실행 중 입력받는다.
$pw = $env:NAIMS_MSSQL_PW
if (-not $pw) {
  $sec = Read-Host -Prompt 'NaimsMig 비밀번호' -AsSecureString
  $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
  try { $pw = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
  finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}
$cs = "Server=tcp:192.168.111.39,1433;Database=NAIMS;User Id=NaimsMig;Password=$pw;Encrypt=false;TrustServerCertificate=true;Connect Timeout=15;"
$cn = New-Object System.Data.SqlClient.SqlConnection $cs
$cn.Open()

$sql = @'
SELECT t.name AS tbl, c.column_id, c.name AS col,
       ty.name AS typ, c.max_length AS ml, c.precision AS pr, c.scale AS sc,
       c.is_nullable AS nb, c.is_identity AS idt, c.is_computed AS cmp
FROM sys.tables t
JOIN sys.columns c ON c.object_id = t.object_id
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
ORDER BY t.name, c.column_id
'@

$cmd = $cn.CreateCommand()
$cmd.CommandText = $sql
$r = $cmd.ExecuteReader()
$out = New-Object System.Text.StringBuilder
$last = ""
while ($r.Read()) {
  $tbl = [string]$r["tbl"]
  if ($tbl -ne $last) { [void]$out.AppendLine(""); [void]$out.AppendLine("### TABLE: $tbl"); $last = $tbl }
  $typ = [string]$r["typ"]
  $ml = [int]$r["ml"]; $pr = [int]$r["pr"]; $sc = [int]$r["sc"]
  $t2 = $typ
  if ($typ -match '^(varchar|char|binary|varbinary)$') {
    if ($ml -eq -1) { $t2 = "$typ(max)" } else { $t2 = "$typ($ml)" }
  } elseif ($typ -match '^(nvarchar|nchar)$') {
    if ($ml -eq -1) { $t2 = "$typ(max)" } else { $t2 = "$typ($($ml/2))" }
  } elseif ($typ -match '^(decimal|numeric)$') {
    $t2 = "$typ($pr,$sc)"
  }
  $flags = ""
  if (-not [bool]$r["nb"]) { $flags += " NOT NULL" }
  if ([bool]$r["idt"]) { $flags += " IDENTITY" }
  if ([bool]$r["cmp"]) { $flags += " COMPUTED" }
  [void]$out.AppendLine(("{0}`t{1}{2}" -f $r["col"], $t2, $flags))
}
$r.Close()

# primary keys
$cmd2 = $cn.CreateCommand()
$cmd2.CommandText = @'
SELECT t.name AS tbl, c.name AS col, ic.key_ordinal
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
JOIN sys.tables t ON t.object_id=i.object_id
WHERE i.is_primary_key = 1
ORDER BY t.name, ic.key_ordinal
'@
$r2 = $cmd2.ExecuteReader()
[void]$out.AppendLine("")
[void]$out.AppendLine("## PRIMARY KEYS")
while ($r2.Read()) { [void]$out.AppendLine(("{0}.{1}" -f $r2["tbl"], $r2["col"])) }
$r2.Close()

$cmd3 = $cn.CreateCommand()
$cmd3.CommandText = @'
SELECT t.name, SUM(p.rows) AS n
FROM sys.tables t
JOIN sys.partitions p ON p.object_id=t.object_id AND p.index_id IN (0,1)
GROUP BY t.name ORDER BY t.name
'@
$r3 = $cmd3.ExecuteReader()
[void]$out.AppendLine("")
[void]$out.AppendLine("## ROW COUNTS")
while ($r3.Read()) { [void]$out.AppendLine(("{0}`t{1}" -f $r3["name"], $r3["n"])) }
$r3.Close()
$cn.Close()

$p = "D:\Caches\UserTemp\claude\F--JKYi-2026-260109-DB----\250d295a-84f0-4a20-b1ee-b46eee45ccb5\scratchpad\mssql_naims_schema.txt"
[System.IO.File]::WriteAllText($p, $out.ToString(), (New-Object System.Text.UTF8Encoding $false))
"written " + $out.Length + " chars"
"tables " + ([regex]::Matches($out.ToString(), '### TABLE:')).Count
