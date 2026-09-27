param([string]$UserLocalAppData, [int]$RequiredGb, [int]$CoreReserveGb, [switch]$Apply, [switch]$CleanupMikeOwned)
@{ optional_can_install = $true } | ConvertTo-Json -Compress
