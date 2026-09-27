[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PrivateRoot,
    [string]$PublicRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$private = (Resolve-Path -LiteralPath $PrivateRoot).ProviderPath.TrimEnd('\', '/')
$public = (Resolve-Path -LiteralPath $PublicRoot).ProviderPath.TrimEnd('\', '/')
if ($private -eq $public -or $public.Length -lt 10) { throw 'Raiz publica insegura.' }

$required = @(
    'src',
    'installer\MikePrerequisites.ps1',
    'local-ai\relay.php',
    'assets\mike-local.ico'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $private $relative))) { throw "Ausente: $relative" }
}

$sourceTarget = Join-Path $public 'src'
if (Test-Path -LiteralPath $sourceTarget) {
    throw 'O destino src ja existe. Exporte para uma arvore limpa para preservar uma revisao auditavel.'
}

$privateSource = Join-Path $private 'src'
New-Item -ItemType Directory -Path $sourceTarget -Force | Out-Null
$sourceFiles = @(Get-ChildItem -LiteralPath $privateSource -File -Recurse | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
})
foreach ($file in $sourceFiles) {
    $relative = $file.FullName.Substring($privateSource.Length).TrimStart([char[]]@('\', '/'))
    $destination = Join-Path $sourceTarget $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
}

foreach ($relative in $required | Where-Object { $_ -ne 'src' }) {
    $destination = Join-Path $public $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $private $relative) -Destination $destination -Force
}

& (Join-Path $public 'scripts\audit-public-release.ps1') -Root $public
if ($LASTEXITCODE -ne 0) { throw 'Auditoria publica reprovada.' }

[pscustomobject]@{
    ok = $true
    source_files = $sourceFiles.Count
    public_root = $public
} | ConvertTo-Json -Compress
