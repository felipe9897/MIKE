[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [switch]$ScanGitHistory
)

$ErrorActionPreference = 'Stop'
$rootPath = (Resolve-Path -LiteralPath $Root).ProviderPath.TrimEnd('\', '/')
$blockedExtensions = @('.exe', '.msi', '.msix', '.pfx', '.p12', '.pem', '.key', '.db', '.sqlite', '.sqlite3', '.dump')
$blockedNames = @('.env', 'id_rsa', 'id_ed25519', 'credentials.json', 'google-credentials.json')
$secretPatterns = @(
    '(?i)-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----',
    '(?i)(password|passwd|senha|secret|client_secret|api_key|access_token)\s*[:=]\s*["''][^"'']{8,}["'']',
    '(?i)github_pat_[A-Za-z0-9_]{20,}',
    '(?i)gh[opsu]_[A-Za-z0-9]{20,}',
    '(?i)sk-[A-Za-z0-9_-]{20,}',
    '(?i)AKIA[0-9A-Z]{16}'
)

$findings = [System.Collections.Generic.List[string]]::new()
$files = Get-ChildItem -LiteralPath $rootPath -Recurse -Force -File | Where-Object {
    $_.FullName -notmatch '[\\/](\.git|bin|obj|node_modules|vendor|coverage|test-results)[\\/]'
}

foreach ($file in $files) {
    $relative = $file.FullName.Substring($rootPath.Length).TrimStart([char[]]@('\', '/'))
    if ($file.Length -gt 20MB) { $findings.Add("large_file:$relative") }
    if ($blockedExtensions -contains $file.Extension.ToLowerInvariant()) { $findings.Add("blocked_extension:$relative") }
    if ($blockedNames -contains $file.Name.ToLowerInvariant()) { $findings.Add("blocked_name:$relative") }
    if ($file.Length -le 5MB -and $file.Extension -notin @('.png', '.jpg', '.jpeg', '.gif', '.webp', '.ico', '.pdf')) {
        $content = [IO.File]::ReadAllText($file.FullName)
        foreach ($pattern in $secretPatterns) {
            if ($content -match $pattern) { $findings.Add("secret_pattern:$relative"); break }
        }
    }
}

if ($ScanGitHistory -and (Get-Command git -ErrorAction SilentlyContinue) -and (Test-Path (Join-Path $rootPath '.git'))) {
    $history = & git -C $rootPath log -p --all --no-ext-diff -- . 2>$null | Out-String
    foreach ($pattern in $secretPatterns) {
        if ($history -match $pattern) { $findings.Add('secret_pattern:git_history'); break }
    }
}

if ($findings.Count -gt 0) {
    $findings | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
    exit 1
}

[pscustomobject]@{ ok = $true; files = $files.Count; root = $rootPath } | ConvertTo-Json -Compress
exit 0
