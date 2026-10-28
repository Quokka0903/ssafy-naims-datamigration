<#
.SYNOPSIS
    Probes the migration source DB (NAIMS) and finds a working connection string.

.DESCRIPTION
    1) Checks TCP reachability first, so a firewall/down-server problem is not
       mistaken for a credential problem.
    2) Builds candidate connection strings across auth modes (Windows integrated /
       SQL login) and encryption options.
    3) Actually connects with each candidate and reports the ones that succeed.
    4) For the first working candidate, prints server version and the largest
       tables so the schema can be inspected.

    Credentials are taken as parameters or prompted for; nothing is written to disk
    unless -OutFile is given (which stores the password in plaintext - keep it out
    of the repository and delete it after use).

    This file is intentionally ASCII-only so it parses correctly under both
    Windows PowerShell 5.1 (ANSI codepage) and PowerShell 7+ (UTF-8).

.PARAMETER ServerHost
    Database server address. Defaults to the sample source server.

.PARAMETER Port
    TCP port. Default 1433.

.PARAMETER Database
    Target database name. Default NAIMS.

.PARAMETER UserId
    SQL login name. If omitted, only Windows integrated auth is attempted.

.PARAMETER Password
    SQL login password (SecureString). If omitted while -UserId is given, prompts.

.PARAMETER ConnectionString
    Use this exact connection string instead of generating candidates.
    Handy for pasting the string produced by Get-DBeaverCredentials.ps1.

.PARAMETER OutFile
    Optional path to save working connection strings. Plaintext password - beware.

.EXAMPLE
    .\Find-NaimsConnectionString.ps1

.EXAMPLE
    .\Find-NaimsConnectionString.ps1 -UserId sa

.EXAMPLE
    .\Find-NaimsConnectionString.ps1 -ConnectionString "Server=tcp:192.168.111.39,1433;Database=NAIMS;User Id=naims;Password=secret;Encrypt=false;TrustServerCertificate=true;"
#>

[CmdletBinding()]
param(
    [string]       $ServerHost = '192.168.111.39',
    [int]          $Port       = 1433,
    [string]       $Database   = 'NAIMS',
    [string]       $UserId,
    [SecureString] $Password,
    [string]       $ConnectionString,
    [string]       $OutFile,
    [int]          $TimeoutSec = 8,
    [int]          $ListTables = 25
)

$ErrorActionPreference = 'Stop'

function Write-Section([string]$Text) {
    Write-Host ''
    Write-Host ('--- ' + $Text + ' ' + ('-' * [Math]::Max(0, 56 - $Text.Length))) -ForegroundColor Cyan
}

function Hide-Password([string]$ConnStr) {
    return ($ConnStr -replace '(?i)(Password\s*=\s*)[^;]*', '$1********')
}

# If an explicit connection string was supplied, derive host/port from it for the
# reachability probe so the two checks stay consistent.
if ($ConnectionString) {
    if ($ConnectionString -match '(?i)Server\s*=\s*(?:tcp:)?([^,;]+)(?:,(\d+))?') {
        $ServerHost = $Matches[1].Trim()
        if ($Matches[2]) { $Port = [int]$Matches[2] }
    }
    if ($ConnectionString -match '(?i)(?:Initial Catalog|Database)\s*=\s*([^;]+)') {
        $Database = $Matches[1].Trim()
    }
}

# ------------------------------------------------------------------
# 1. TCP reachability
# ------------------------------------------------------------------
Write-Section "TCP reachability  ${ServerHost}:${Port}"

$tcpOk = $false
try {
    $client = [System.Net.Sockets.TcpClient]::new()
    $task   = $client.ConnectAsync($ServerHost, $Port)
    if ($task.Wait([TimeSpan]::FromSeconds($TimeoutSec))) {
        $tcpOk = $client.Connected
    }
    $client.Close()
}
catch {
    $tcpOk = $false
}

if ($tcpOk) {
    Write-Host '  [OK] Port is open.' -ForegroundColor Green
}
else {
    Write-Host '  [FAIL] Cannot reach the port (server down / firewall / wrong address).' -ForegroundColor Red
    Write-Host '         This is a network issue, not a connection-string issue.' -ForegroundColor DarkGray
    return
}

# ------------------------------------------------------------------
# 2. Build candidates
# ------------------------------------------------------------------
Write-Section 'Building candidates'

$candidates = [System.Collections.Generic.List[object]]::new()

if ($ConnectionString) {
    $candidates.Add([pscustomobject]@{
        Auth             = 'Supplied connection string'
        Encryption       = '(as given)'
        ConnectionString = $ConnectionString
    })
    Write-Host '  Using the supplied connection string as-is.'
}
else {
    if ($UserId -and -not $Password) {
        $Password = Read-Host -Prompt "  Password for '$UserId'" -AsSecureString
    }

    $plainPassword = $null
    if ($Password) {
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
        try   { $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    }

    $server = "tcp:${ServerHost},${Port}"

    # Encryption behaviour depends on server configuration, so try both.
    $encryptionModes = @(
        @{ Label = 'Encrypt=false';                              Fragment = 'Encrypt=false;' },
        @{ Label = 'Encrypt=true + TrustServerCertificate=true'; Fragment = 'Encrypt=true;TrustServerCertificate=true;' }
    )

    foreach ($enc in $encryptionModes) {
        $candidates.Add([pscustomobject]@{
            Auth             = 'Windows integrated'
            Encryption       = $enc.Label
            ConnectionString = "Server=$server;Database=$Database;Integrated Security=true;$($enc.Fragment)Connect Timeout=$TimeoutSec;"
        })

        if ($UserId -and $plainPassword) {
            $candidates.Add([pscustomobject]@{
                Auth             = "SQL login ($UserId)"
                Encryption       = $enc.Label
                ConnectionString = "Server=$server;Database=$Database;User Id=$UserId;Password=$plainPassword;$($enc.Fragment)Connect Timeout=$TimeoutSec;"
            })
        }
    }

    Write-Host "  Generated $($candidates.Count) candidate(s)."
    if (-not $UserId) {
        Write-Host '  [i] Pass -UserId <login> to also try SQL authentication.' -ForegroundColor DarkGray
    }
}

# ------------------------------------------------------------------
# 3. Try each candidate
# ------------------------------------------------------------------
Write-Section 'Connecting'

$succeeded = [System.Collections.Generic.List[object]]::new()

foreach ($c in $candidates) {
    Write-Host "  * $($c.Auth) / $($c.Encryption) ... " -NoNewline

    $conn = $null
    try {
        $conn = [System.Data.SqlClient.SqlConnection]::new($c.ConnectionString)
        $conn.Open()

        $cmd = $conn.CreateCommand()
        $cmd.CommandText = 'SELECT @@VERSION'
        $version     = [string]$cmd.ExecuteScalar()
        $versionHead = ($version -split "`n")[0].Trim()

        Write-Host 'OK' -ForegroundColor Green
        $succeeded.Add([pscustomobject]@{
            Auth             = $c.Auth
            Encryption       = $c.Encryption
            ConnectionString = $c.ConnectionString
            ServerVersion    = $versionHead
        })
    }
    catch {
        $msg = $_.Exception.Message
        if ($msg.Length -gt 90) { $msg = $msg.Substring(0, 90) + '...' }
        Write-Host "failed - $msg" -ForegroundColor DarkYellow
    }
    finally {
        if ($conn) { $conn.Dispose() }
    }
}

# ------------------------------------------------------------------
# 4. Report
# ------------------------------------------------------------------
Write-Section 'Result'

if ($succeeded.Count -eq 0) {
    Write-Host '  [FAIL] No candidate connected.' -ForegroundColor Red
    Write-Host ''
    Write-Host '  Check the following:' -ForegroundColor DarkGray
    Write-Host '   - Is SQL Server running in mixed authentication mode?' -ForegroundColor DarkGray
    Write-Host '   - Are the login and password correct, with rights on the database?' -ForegroundColor DarkGray
    Write-Host "   - Does database '$Database' exist?" -ForegroundColor DarkGray
    return
}

$best = $succeeded[0]
Write-Host "  [OK] $($succeeded.Count) working combination(s). Recommended:" -ForegroundColor Green
Write-Host ''
Write-Host "     Auth       : $($best.Auth)"
Write-Host "     Encryption : $($best.Encryption)"
Write-Host "     Version    : $($best.ServerVersion)"
Write-Host ''
Write-Host '     Connection string (password masked):' -ForegroundColor Cyan
Write-Host "     $(Hide-Password $best.ConnectionString)" -ForegroundColor White

# ------------------------------------------------------------------
# 5. Inspect schema through the working connection
# ------------------------------------------------------------------
Write-Section "Largest tables (top $ListTables)"
try {
    $conn = [System.Data.SqlClient.SqlConnection]::new($best.ConnectionString)
    $conn.Open()
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = @"
SELECT TOP ($ListTables)
       s.name  AS SchemaName,
       t.name  AS TableName,
       SUM(p.rows) AS [RowCount]
  FROM sys.tables t
  JOIN sys.schemas s ON s.schema_id = t.schema_id
  JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
 GROUP BY s.name, t.name
 ORDER BY SUM(p.rows) DESC, t.name
"@
    $reader = $cmd.ExecuteReader()
    $table  = [System.Data.DataTable]::new()
    $table.Load($reader)

    $cmd2 = $conn.CreateCommand()
    $cmd2.CommandText = 'SELECT COUNT(*) FROM sys.tables'
    $totalTables = [int]$cmd2.ExecuteScalar()
    $conn.Dispose()

    if ($table.Rows.Count -eq 0) {
        Write-Host '  (no tables)' -ForegroundColor DarkGray
    }
    else {
        $table | Format-Table -AutoSize | Out-String | Write-Host
        Write-Host "  Showing $($table.Rows.Count) of $totalTables tables (by row count desc)." -ForegroundColor DarkGray
    }
}
catch {
    Write-Host "  Could not list tables: $($_.Exception.Message)" -ForegroundColor DarkYellow
}

# ------------------------------------------------------------------
# 6. Optional save
# ------------------------------------------------------------------
if ($OutFile) {
    Write-Section 'Saving'
    Write-Host '  [!] The password is written in plaintext. Do not commit this file.' -ForegroundColor Yellow

    $lines = @(
        '# NAIMS source DB connection strings - probe result',
        "# Server: ${ServerHost}:${Port}  Database: $Database",
        '# WARNING: contains plaintext password. Delete after use.',
        ''
    )
    foreach ($s in $succeeded) {
        $lines += "# $($s.Auth) / $($s.Encryption)"
        $lines += $s.ConnectionString
        $lines += ''
    }
    Set-Content -Path $OutFile -Value $lines -Encoding UTF8
    Write-Host "  Saved: $OutFile" -ForegroundColor Green
}

Write-Host ''
Write-Host 'Paste the connection string into the [DB -> DB] tab, "source connection" field.' -ForegroundColor Cyan
Write-Host ''
