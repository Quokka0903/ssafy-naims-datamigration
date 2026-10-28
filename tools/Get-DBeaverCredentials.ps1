<#
.SYNOPSIS
    Reads DB connection credentials stored in local DBeaver and builds a
    Microsoft.Data.SqlClient connection string for the migration tool.

.DESCRIPTION
    DBeaver keeps connection definitions in data-sources.json and credentials in
    credentials-config.json. The latter is encrypted with AES-128-CBC using a key
    that is a constant embedded in DBeaver (obfuscation, not a per-user secret).

    This script decrypts that file, matches each connection with its credentials,
    and prints a ready-to-use connection string.

    NOTE: Use only for your own credentials on your own machine.
    Output can contain a plaintext password. It is masked by default;
    pass -ShowSecret to reveal it.

    This file is intentionally ASCII-only so it parses correctly under both
    Windows PowerShell 5.1 (ANSI codepage) and PowerShell 7+ (UTF-8).

.PARAMETER Filter
    Substring match against connection name / host / database. Omit to list all.

.PARAMETER ShowSecret
    Print the real password instead of asterisks.

.PARAMETER Workspace
    DBeaver workspace path. Defaults to the standard location.

.EXAMPLE
    .\Get-DBeaverCredentials.ps1

.EXAMPLE
    .\Get-DBeaverCredentials.ps1 -Filter 192.168.111.39 -ShowSecret
#>

[CmdletBinding()]
param(
    [string] $Filter,
    [switch] $ShowSecret,
    [string] $Workspace = "$env:APPDATA\DBeaverData\workspace6\General"
)

$ErrorActionPreference = 'Stop'

function Write-Section([string]$Text) {
    Write-Host ''
    Write-Host ('--- ' + $Text + ' ' + ('-' * [Math]::Max(0, 56 - $Text.Length))) -ForegroundColor Cyan
}

$dsPath   = Join-Path $Workspace '.dbeaver\data-sources.json'
$credPath = Join-Path $Workspace '.dbeaver\credentials-config.json'

if (-not (Test-Path $dsPath)) {
    Write-Host "[FAIL] data-sources.json not found: $dsPath" -ForegroundColor Red
    Write-Host "       Pass -Workspace <path> to point at your DBeaver workspace." -ForegroundColor DarkGray
    return
}

# ------------------------------------------------------------------
# 1. Decrypt the credentials store
#    Format: first 16 bytes = IV, remainder = AES-128-CBC ciphertext.
# ------------------------------------------------------------------
$credentials = @{}

if (Test-Path $credPath) {
    try {
        $raw = [System.IO.File]::ReadAllBytes($credPath)

        # Constant local key embedded in DBeaver (not a per-user secret)
        $keyHex = 'babb4a9f774ab853c96c2d653dfe544a'
        $key    = [byte[]]::new(16)
        for ($i = 0; $i -lt 16; $i++) {
            $key[$i] = [Convert]::ToByte($keyHex.Substring($i * 2, 2), 16)
        }

        $aes         = [System.Security.Cryptography.Aes]::Create()
        $aes.Mode    = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::None
        $aes.Key     = $key
        $aes.IV      = $raw[0..15]

        $decryptor = $aes.CreateDecryptor()
        $cipher    = $raw[16..($raw.Length - 1)]
        $plainRaw  = $decryptor.TransformFinalBlock($cipher, 0, $cipher.Length)
        $aes.Dispose()

        # Trim padding: keep everything up to the last closing brace.
        $text = [System.Text.Encoding]::UTF8.GetString($plainRaw)
        $end  = $text.LastIndexOf('}')
        if ($end -ge 0) { $text = $text.Substring(0, $end + 1) }

        $json = $text | ConvertFrom-Json
        foreach ($p in $json.PSObject.Properties) {
            $credentials[$p.Name] = $p.Value
        }
        Write-Host "[OK] Decrypted credentials for $($credentials.Count) connection(s)." -ForegroundColor Green
    }
    catch {
        Write-Host "[!] Could not decrypt credentials: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host "    Showing connection metadata only." -ForegroundColor DarkGray
    }
}
else {
    Write-Host "[!] credentials-config.json not found. Showing connection metadata only." -ForegroundColor Yellow
}

# ------------------------------------------------------------------
# 2. Read connection definitions and merge with credentials
# ------------------------------------------------------------------
$ds      = Get-Content $dsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$results = [System.Collections.Generic.List[object]]::new()

foreach ($p in $ds.connections.PSObject.Properties) {
    $id   = $p.Name
    $conn = $p.Value
    $cfg  = $conn.configuration

    $user = $null
    $pass = $null
    if ($credentials.ContainsKey($id)) {
        $c = $credentials[$id]
        if ($c.'#connection') {
            $user = $c.'#connection'.user
            $pass = $c.'#connection'.password
        }
    }

    $results.Add([pscustomobject]@{
        Name      = $conn.name
        Provider  = $conn.provider
        DbHost    = $cfg.host
        Port      = $cfg.port
        Database  = $cfg.database
        Auth      = $cfg.'auth-model'
        User      = $user
        Password  = $pass
        TrustCert = $cfg.'provider-properties'.sslTrustServerCertificate
    })
}

if ($Filter) {
    $results = @($results | Where-Object {
        $_.Name     -like "*$Filter*" -or
        $_.DbHost   -like "*$Filter*" -or
        $_.Database -like "*$Filter*"
    })
}

if (@($results).Count -eq 0) {
    Write-Host "[FAIL] No connection matched filter: '$Filter'" -ForegroundColor Red
    return
}

# ------------------------------------------------------------------
# 3. List matched connections
# ------------------------------------------------------------------
Write-Section 'Saved connections'

$results |
    Select-Object Name, Provider, DbHost, Port, Database, Auth,
                  @{ N = 'User';     E = { if ($_.User) { $_.User } else { '(none)' } } },
                  @{ N = 'Password'; E = {
                        if (-not $_.Password) { '(none)' }
                        elseif ($ShowSecret)  { $_.Password }
                        else                  { '********' }
                     } } |
    Format-Table -AutoSize | Out-String | Write-Host

# ------------------------------------------------------------------
# 4. Build SQL Server connection strings
# ------------------------------------------------------------------
Write-Section 'Microsoft.Data.SqlClient connection strings'

foreach ($r in $results) {
    if ($r.Provider -ne 'sqlserver') { continue }

    $server = "tcp:$($r.DbHost),$($r.Port)"

    if ($r.Auth -eq 'sqlserver_database' -and $r.User) {
        $shownPass = if ($ShowSecret) { $r.Password } else { '********' }
        $cs = "Server=$server;Database=$($r.Database);User Id=$($r.User);Password=$shownPass;Encrypt=false;TrustServerCertificate=true;Connect Timeout=15;"
    }
    else {
        $cs = "Server=$server;Database=$($r.Database);Integrated Security=true;Encrypt=false;TrustServerCertificate=true;Connect Timeout=15;"
    }

    Write-Host ''
    Write-Host "  [$($r.Name)]  $($r.DbHost)/$($r.Database)" -ForegroundColor Yellow
    Write-Host "  $cs" -ForegroundColor White
}

if (-not $ShowSecret) {
    Write-Host ''
    Write-Host '  [i] Add -ShowSecret to reveal the actual password.' -ForegroundColor DarkGray
}
Write-Host ''
