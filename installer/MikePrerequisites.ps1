[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$InstallMissing,
    [switch]$PostInstall,
    [string]$InstallRoot = (Join-Path ${env:ProgramFiles} 'MikeLocal'),
    [string]$UserLocalAppData = $env:LOCALAPPDATA
)

$ProgressPreference = 'SilentlyContinue'
$WindowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $WindowsPowerShell)) { throw "Windows PowerShell não encontrado em $WindowsPowerShell" }

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($UserLocalAppData) { $env:LOCALAPPDATA = [IO.Path]::GetFullPath($UserLocalAppData) }

function Result([string]$Name, [bool]$Ok, [string]$Detail) {
    [pscustomobject]@{ prerequisite = $Name; ok = $Ok; detail = $Detail }
}

$results = [System.Collections.Generic.List[object]]::new()
$is64 = [Environment]::Is64BitOperatingSystem

# WebView2 is required by Mike.Desktop. The Evergreen runtime is installed
# machine-wide or per-user; checking both registry views avoids false failures.
$webView2 = $false
foreach ($key in @(
    'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F1E75B3A-0B1B-4A4F-8A90-1A7D1BA3B7A5}',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F1E75B3A-0B1B-4A4F-8A90-1A7D1BA3B7A5}',
    'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F1E75B3A-0B1B-4A4F-8A90-1A7D1BA3B7A5}'
)) { if (Test-Path $key) { $webView2 = $true } }
if (-not $webView2) {
    $webView2 = @(
        "$env:ProgramFiles\Microsoft\EdgeWebView\Application",
        "${env:ProgramFiles(x86)}\Microsoft\EdgeWebView\Application"
    ) | Where-Object { $_ -and (Test-Path $_) } | ForEach-Object { $true } | Select-Object -First 1
}
if (-not $webView2 -and $InstallMissing -and (Get-Command winget -ErrorAction SilentlyContinue)) {
    if ($PSCmdlet.ShouldProcess('Microsoft Edge WebView2 Runtime', 'instalar via winget')) {
        & winget install --id Microsoft.EdgeWebView2Runtime --exact --silent --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -eq 0) { $webView2 = $true }
    }
}
if (-not $webView2 -and $InstallMissing) {
    $webViewInstaller = Join-Path $env:TEMP 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $webViewInstaller -UseBasicParsing
    if ($PSCmdlet.ShouldProcess('Microsoft Edge WebView2 Runtime', 'instalar pelo instalador oficial')) {
        $process = Start-Process -FilePath $webViewInstaller -ArgumentList '/silent','/install' -Wait -PassThru
        if ($process.ExitCode -in 0,3010) { $webView2 = $true }
    }
}
$results.Add((Result 'WebView2 Evergreen Runtime' ([bool]$webView2) ($(if($webView2){'detectado'}else{'ausente; instale Microsoft Edge WebView2 Runtime'}))))

$ollama = Get-Command ollama -ErrorAction SilentlyContinue
if (-not $ollama) {
    foreach ($candidate in @("$env:LOCALAPPDATA\Programs\Ollama\ollama.exe", "$env:LOCALAPPDATA\Ollama\ollama.exe")) {
        if (Test-Path $candidate) { $ollama = Get-Command $candidate; break }
    }
}
$ollamaOk = $null -ne $ollama
$ollamaDetail = if ($ollamaOk) { $ollama.Source } else { 'ollama.exe ausente' }
if ($ollamaOk) {
    $server = Join-Path (Split-Path $ollama.Source) 'lib\ollama\llama-server.exe'
    if (-not (Test-Path $server)) { $ollamaOk = $false; $ollamaDetail = 'runtime incompleto: lib\ollama\llama-server.exe ausente' }
}
if (-not $ollamaOk -and $InstallMissing -and (Get-Command winget -ErrorAction SilentlyContinue)) {
    if ($PSCmdlet.ShouldProcess('Ollama', 'instalar/atualizar via winget')) {
        & winget install --id Ollama.Ollama --exact --silent --accept-package-agreements --accept-source-agreements
    }
}
if (-not $ollamaOk -and $InstallMissing) {
    $ollamaInstaller = Join-Path $env:TEMP 'OllamaSetup.exe'
    Invoke-WebRequest -Uri 'https://ollama.com/download/OllamaSetup.exe' -OutFile $ollamaInstaller -UseBasicParsing
    if ($PSCmdlet.ShouldProcess('Ollama', 'instalar pelo instalador oficial')) {
        $process = Start-Process -FilePath $ollamaInstaller -ArgumentList '/VERYSILENT','/NORESTART' -Wait -PassThru
        if ($process.ExitCode -notin 0,3010) { throw "O instalador do Ollama falhou: $($process.ExitCode)" }
    }
    foreach ($candidate in @("$env:LOCALAPPDATA\Programs\Ollama\ollama.exe", "$env:LOCALAPPDATA\Ollama\ollama.exe")) {
        if (Test-Path $candidate) { $ollama = Get-Command $candidate; break }
    }
    if ($ollama) {
        $server = Join-Path (Split-Path $ollama.Source) 'lib\ollama\llama-server.exe'
        $ollamaOk = Test-Path $server
        $ollamaDetail = if ($ollamaOk) { $ollama.Source } else { 'runtime incompleto após instalação' }
    }
}
$results.Add((Result 'Ollama runtime completo' $ollamaOk $ollamaDetail))

$tagsOk = $false
if ($ollamaOk) {
    try {
        Start-Process -FilePath $ollama.Source -ArgumentList 'serve' -WindowStyle Hidden | Out-Null
        Start-Sleep -Seconds 3
    } catch { }
}
try {
    $tags = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/tags' -TimeoutSec 5
    $tagsOk = $null -ne $tags.models
} catch { }
$results.Add((Result 'Ollama API local' $tagsOk ($(if($tagsOk){'127.0.0.1:11434 respondeu'}else{'inicie Ollama antes do teste'}))))

$hermesCandidates = @(
    "$env:LOCALAPPDATA\hermes\bin\hermes.exe",
    "$env:LOCALAPPDATA\hermes\hermes.exe",
    "$env:LOCALAPPDATA\hermes\hermes-agent.exe",
    "$env:LOCALAPPDATA\Programs\hermes\hermes.exe"
)
$hermes = $hermesCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $hermes) { $cmd = Get-Command hermes -ErrorAction SilentlyContinue; if ($cmd) { $hermes = $cmd.Source } }
$ramGb = [math]::Floor((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
$video = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'NVIDIA|AMD|Radeon|Intel.+Arc' } | Sort-Object AdapterRAM -Descending | Select-Object -First 1
$vramGb = if ($video -and $video.AdapterRAM) { [math]::Round([double]$video.AdapterRAM / 1GB, 1) } else { 0 }
$nvidiaSmi = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
if ($nvidiaSmi) {
    try {
        $vramMb = [double]((& $nvidiaSmi.Source --query-gpu=memory.total --format=csv,noheader,nounits | Select-Object -First 1).Trim())
        if ($vramMb -gt 0) { $vramGb = [math]::Round($vramMb / 1024, 1) }
    } catch {}
}
$profileModels = if ($ramGb -ge 24 -or $vramGb -ge 10) {
    [ordered]@{ fast='qwen2.5:3b'; balanced='qwen2.5:7b'; heavy='deepseek-r1:14b' }
} elseif ($ramGb -ge 10 -or $vramGb -ge 4) {
    [ordered]@{ fast='qwen2.5:1.5b'; balanced='qwen2.5:3b'; heavy='deepseek-r1:7b' }
} else {
    [ordered]@{ fast='qwen2.5:0.5b'; balanced='qwen2.5:1.5b'; heavy='deepseek-r1:1.5b' }
}
$ollamaDrive = [System.IO.Path]::GetPathRoot([Environment]::GetFolderPath('LocalApplicationData'))
$freeDiskGb = [math]::Round((Get-CimInstance Win32_LogicalDisk -Filter ("DeviceID='" + $ollamaDrive.TrimEnd('\') + "'") -ErrorAction SilentlyContinue).FreeSpace / 1GB, 1)
$estimatedModelGb = if ($profileModels.heavy -match ':14b$') { 18 } elseif ($profileModels.heavy -match ':7b$') { 10 } else { 5 }
$diskReady = $freeDiskGb -ge $estimatedModelGb
$results.Add((Result 'Espaço para os três modelos' $diskReady ("livres: {0} GB; necessário estimado: {1} GB" -f $freeDiskGb,$estimatedModelGb)))
$modelOk = $false
if ($InstallMissing -and $ollamaOk) {
    if (-not $diskReady) { Write-Warning "Espaço insuficiente para baixar os três perfis de modelos sem risco de lotar o disco." }
    foreach ($role in $profileModels.Keys) {
        $model = [string]$profileModels[$role]
        $present = $tagsOk -and @($tags.models | Where-Object { $_.name -eq $model }).Count -gt 0
        if (-not $present -and $diskReady -and $PSCmdlet.ShouldProcess($model, "baixar modelo $role adaptado ao hardware (GGUF quantizado pelo Ollama)")) {
            & $ollama.Source pull $model
        }
    }
}
try {
    $tags = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/tags' -TimeoutSec 10
    $tagsOk = $null -ne $tags.models
} catch { $tagsOk = $false }
$missingProfileModels = @($profileModels.Values | Where-Object { $wanted=$_; -not @($tags.models | Where-Object { $_.name -eq $wanted }).Count })
$modelOk = $tagsOk -and $missingProfileModels.Count -eq 0
$hermesModelOk = $tagsOk -and @($tags.models | Where-Object { $_.name -match '^llama3\.2:3b$' }).Count -gt 0
if ($ollamaOk -and $InstallMissing -and -not $hermesModelOk) {
    & $ollama.Source pull 'llama3.2:3b'
    $hermesModelOk = $LASTEXITCODE -eq 0
}
$inferenceOk = $false
if ($ollamaOk -and $modelOk) {
    $gpuAdapter = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'NVIDIA|AMD|Radeon|Intel.+Arc' } | Select-Object -First 1
    $gpuRuntimeDir = Join-Path (Split-Path -Parent $ollama.Source) 'lib\ollama'
    $gpuRuntimeReady = (Test-Path (Join-Path $gpuRuntimeDir 'cuda_v12')) -or
        (Test-Path (Join-Path $gpuRuntimeDir 'cuda_v13')) -or
        (Test-Path (Join-Path $gpuRuntimeDir 'rocm_v7_1')) -or
        (Test-Path (Join-Path $gpuRuntimeDir 'vulkan'))
    if ($gpuAdapter -and $gpuRuntimeReady) {
        # Uma falha antiga podia deixar este override permanente e desativar a
        # GPU para sempre. Toda reparacao retesta primeiro o backend automatico.
        [Environment]::SetEnvironmentVariable('OLLAMA_LLM_LIBRARY', $null, 'User')
        Remove-Item Env:OLLAMA_LLM_LIBRARY -ErrorAction SilentlyContinue
        Get-Process ollama -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
        Start-Process -FilePath $ollama.Source -ArgumentList 'serve' -WindowStyle Hidden | Out-Null
        Start-Sleep -Seconds 3
    }
    $smokeBody = @{ model = [string]$profileModels.fast; prompt = 'Responda somente OK'; stream = $false } | ConvertTo-Json
    try {
        $smoke = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/generate' -Method Post -ContentType 'application/json' -Body $smokeBody -TimeoutSec 90
        $inferenceOk = -not [string]::IsNullOrWhiteSpace([string]$smoke.response)
    } catch {
        # CUDA antigo pode falhar mesmo com a GPU detectada. Antes de cair para
        # CPU, tente Vulkan: em placas NVIDIA/AMD/Intel antigas ele costuma usar
        # toda a GPU sem exigir a versao mais nova do driver CUDA/ROCm.
        [Environment]::SetEnvironmentVariable('OLLAMA_VULKAN', '1', 'User')
        [Environment]::SetEnvironmentVariable('OLLAMA_LLM_LIBRARY', 'vulkan', 'User')
        $env:OLLAMA_VULKAN = '1'
        $env:OLLAMA_LLM_LIBRARY = 'vulkan'
        Get-Process ollama -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
        Start-Process -FilePath $ollama.Source -ArgumentList 'serve' -WindowStyle Hidden | Out-Null
        Start-Sleep -Seconds 3
        try {
            $smoke = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/generate' -Method Post -ContentType 'application/json' -Body $smokeBody -TimeoutSec 180
            $inferenceOk = -not [string]::IsNullOrWhiteSpace([string]$smoke.response)
        } catch {
            [Environment]::SetEnvironmentVariable('OLLAMA_LLM_LIBRARY', 'cpu', 'User')
            $env:OLLAMA_LLM_LIBRARY = 'cpu'
            Get-Process ollama -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
            Start-Process -FilePath $ollama.Source -ArgumentList 'serve' -WindowStyle Hidden | Out-Null
            Start-Sleep -Seconds 3
            try {
                $smoke = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/generate' -Method Post -ContentType 'application/json' -Body $smokeBody -TimeoutSec 180
                $inferenceOk = -not [string]::IsNullOrWhiteSpace([string]$smoke.response)
            } catch { $inferenceOk = $false }
        }
    }
}
$results.Add((Result 'Inferência local real' $inferenceOk ($(if($inferenceOk){'resposta Ollama validada'}else{'modelo instalado, mas inferência falhou'}))))
$results.Add((Result 'Modelo local Hermes' $hermesModelOk ($(if($hermesModelOk){'llama3.2:3b disponível'}else{'llama3.2:3b ausente'}))))

if ($InstallMissing -and -not $hermes) {
    $hermesInstaller = Join-Path $InstallRoot 'tools\install-hermes-official.ps1'
    if (Test-Path -LiteralPath $hermesInstaller) {
        & $WindowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $hermesInstaller -Install -SkipSetup
        $hermes = $hermesCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
}
$results.Add((Result 'Hermes Agent oficial' ([bool]$hermes) ($(if($hermes){$hermes}else{'instalação Hermes não concluída'}))))
$profileSummary = "rápido={0}; equilibrado={1}; raciocínio={2}; formato GGUF quantizado" -f $profileModels.fast,$profileModels.balanced,$profileModels.heavy
$results.Add((Result 'Três perfis de modelos locais' $modelOk ($(if($modelOk){$profileSummary}else{"faltando: " + ($missingProfileModels -join ', ')}))))

# Hermes Computer Use: install/repair from the official CUA installer, then
# start its per-user daemon and require a real health report. The driver does
# not need admin rights; Windows/UAC remains reserved for explicit R3 actions.
$cua = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Cua\cua-driver\bin\cua-driver.exe'),
    (Join-Path $env:USERPROFILE '.local\bin\cua-driver.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $cua -and $InstallMissing) {
    Write-Host 'Computer Use ausente; baixando o instalador oficial CUA...' -ForegroundColor Cyan
    $cuaInstall = Join-Path $env:TEMP 'mike-cua-install.ps1'
    Invoke-WebRequest -Uri 'https://cua.ai/driver/install.ps1' -OutFile $cuaInstall -UseBasicParsing
    & $WindowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $cuaInstall
    $cua = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Cua\cua-driver\bin\cua-driver.exe'),
        (Join-Path $env:USERPROFILE '.local\bin\cua-driver.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
$cuaOk = $false
$cuaDetail = 'driver ausente'
if ($cua) {
    Write-Host 'Validando e iniciando Computer Use...' -ForegroundColor Cyan
    $autostartStatus = (& $cua autostart status 2>&1 | Out-String)
    if ($autostartStatus -notmatch 'registered') {
        & $cua autostart enable | Out-Host
    }
    & $cua autostart kick | Out-Host
    Start-Sleep -Seconds 2
    try {
        $healthRaw = (& $cua call health_report '{}' 2>&1 | Out-String)
        $cuaOk = $LASTEXITCODE -eq 0 -and $healthRaw -match '"overall"\s*:\s*"ok"'
        $cuaDetail = if ($cuaOk) { "health ok; $cua" } else { "health incompleto: $($healthRaw.Trim())" }
    } catch { $cuaDetail = $_.Exception.Message }
}
$results.Add((Result 'Hermes Computer Use' $cuaOk $cuaDetail))

# Install VS Code when necessary and install/update the bundled Mike extension.
$code = Get-Command code.cmd -ErrorAction SilentlyContinue
$knownCode = Join-Path $env:LOCALAPPDATA 'Programs\Microsoft VS Code\bin\code.cmd'
if (-not $code -and (Test-Path -LiteralPath $knownCode)) { $code = Get-Item -LiteralPath $knownCode }
if (-not $code -and $InstallMissing) {
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Host 'Instalando Visual Studio Code para programacao com a Mike...' -ForegroundColor Cyan
        & $winget.Source install --id Microsoft.VisualStudioCode --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity | Out-Host
        if (Test-Path -LiteralPath $knownCode) { $code = Get-Item -LiteralPath $knownCode }
    }
}
if (-not $code -and $InstallMissing) {
    try {
        $vscodeSetup = Join-Path $env:TEMP 'Mike-VSCodeUserSetup-x64.exe'
        Write-Host 'Winget indisponivel; baixando VS Code do canal oficial Microsoft...' -ForegroundColor Cyan
        Invoke-WebRequest -Uri 'https://update.code.visualstudio.com/latest/win32-x64-user/stable' -OutFile $vscodeSetup -UseBasicParsing -TimeoutSec 180
        $signature = Get-AuthenticodeSignature -FilePath $vscodeSetup
        if ($signature.Status -ne 'Valid' -or [string]$signature.SignerCertificate.Subject -notmatch 'Microsoft') {
            throw 'Assinatura Microsoft do instalador VS Code nao foi validada.'
        }
        $vscodeProcess = Start-Process -FilePath $vscodeSetup -ArgumentList '/VERYSILENT','/NORESTART','/MERGETASKS=!runcode' -Wait -PassThru
        if ($vscodeProcess.ExitCode -in 0,3010 -and (Test-Path -LiteralPath $knownCode)) { $code = Get-Item -LiteralPath $knownCode }
    } catch {
        Write-Warning ("VS Code sera reparado depois: " + $_.Exception.Message)
    }
}
$vsix = Join-Path $InstallRoot 'tools\mike-local-ai-1.2.8.vsix'
$vscodeOk = $true
$vscodeDetail = 'VS Code não pôde ser instalado automaticamente; pacote da extensão preservado'
if ($code -and (Test-Path -LiteralPath $vsix)) {
    Write-Host 'Instalando integracao Mike no VS Code...' -ForegroundColor Cyan
    $codePath = if ($code -is [System.Management.Automation.CommandInfo]) { $code.Source } else { $code.FullName }
    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $codePath --install-extension $vsix --force 2>&1 | Out-Host
        $vscodeExit = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousErrorAction }
    $vscodeOk = $vscodeExit -eq 0
    $vscodeDetail = if ($vscodeOk) { 'extensao @mike 1.2.8 instalada' } else { 'VS Code recusou a instalacao da extensao' }
}
$results.Add((Result 'Extensao Mike para VS Code' $vscodeOk $vscodeDetail))

# A descoberta automática usa um farol UDP e o relay privado usa SMB somente
# dentro da sub-rede. As regras são criadas uma vez durante o UAC do instalador;
# depois o cluster trabalha silenciosamente e não abre portas para a Internet.
$clusterFirewallOk = $false
$clusterFirewallDetail = 'requer execução administrativa durante a instalação'
if ($InstallMissing -or $PostInstall) {
    try {
        $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if ($isAdmin) {
            foreach ($rule in @(
                @{ Name='MikeLocal-LAN-Discovery'; Protocol='UDP'; Port='47820'; Description='Descoberta automática de outros computadores Mike na rede local' },
                @{ Name='MikeLocal-LAN-Relay'; Protocol='TCP'; Port='445'; Description='Relay privado Mike entre computadores da mesma rede local' }
            )) {
                Remove-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue
                New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Action Allow -Protocol $rule.Protocol -LocalPort $rule.Port -RemoteAddress LocalSubnet -Profile Private,Domain -Description $rule.Description | Out-Null
            }
            $clusterFirewallOk = $true
            $clusterFirewallDetail = 'descoberta UDP 47820 e relay TCP 445 limitados à sub-rede local'
        }
    } catch { $clusterFirewallDetail = $_.Exception.Message }
}
$results.Add((Result 'Sincronização automática entre PCs' $clusterFirewallOk $clusterFirewallDetail))

$mangaArchive = Join-Path $InstallRoot 'tools\mike-manga-runtime.zip'
$mangaRoot = Join-Path $env:LOCALAPPDATA 'MikeLocal\manga-translate'
$mangaOk = $false
$mangaDetail = 'pacote do tradutor ausente'
if (Test-Path -LiteralPath $mangaArchive) {
    Write-Host 'Preparando tradutor de mangá, OCR e renderização...' -ForegroundColor Cyan
    Expand-Archive -LiteralPath $mangaArchive -DestinationPath (Join-Path $env:LOCALAPPDATA 'MikeLocal') -Force
    $mangaInstaller = Join-Path $mangaRoot 'scripts\install-manga-stack.ps1'
    if (Test-Path -LiteralPath $mangaInstaller) {
        $ramGb = [math]::Floor((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
        $mangaModel = if ($ramGb -ge 24 -or $vramGb -ge 6) { 'qwen2.5:7b' } elseif ($ramGb -ge 12 -or $vramGb -ge 3) { 'qwen2.5:3b' } else { 'qwen2.5:1.5b' }
        & $WindowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $mangaInstaller -ModuleRoot $mangaRoot -OllamaModel $mangaModel
        $mangaExit = $LASTEXITCODE
        $pythonReady = @(Get-ChildItem "$env:LOCALAPPDATA\Programs\Python\Python3*\python.exe" -ErrorAction SilentlyContinue).Count -gt 0
        $mangaOk = $mangaExit -eq 0 -and $pythonReady -and `
            (Test-Path -LiteralPath (Join-Path $mangaRoot 'vendor\koharu\koharu.exe')) -and `
            (Test-Path -LiteralPath (Join-Path $mangaRoot 'vendor\tesseract\bin\tesseract.exe'))
        $mangaDetail = if ($mangaOk) { "módulo instalado; modelo adaptado $mangaModel" } else { "instalação retornou código $mangaExit" }
    }
}
$results.Add((Result 'Tradutor de mangá/webtoon' $mangaOk $mangaDetail))

# Compatibility backend: the mature Mike HTTP agent owns the advanced routes
# that have not yet moved into the native .NET bridge. It is embedded in the
# MSI and runs backend-only: no browser window and no second desktop app.
$backendCore = Join-Path $InstallRoot 'tools\mike-local-ai-backend.ps1'
$backendOk = $false
try {
    $status = Invoke-RestMethod -Uri 'http://127.0.0.1:47885/ui/status' -TimeoutSec 4
    $backendOk = [bool]$status.ok
} catch { }
if ($InstallMissing -and -not $backendOk -and (Test-Path -LiteralPath $backendCore)) {
    Write-Host 'Preparando o backend completo de ferramentas...' -ForegroundColor Cyan
    $backendRoot = Join-Path $UserLocalAppData 'MINIKE-Local-AI'
    $backendLogRoot = Join-Path $backendRoot 'logs'
    New-Item -ItemType Directory -Path $backendLogRoot -Force | Out-Null
    $backendArgumentLine = '-NoProfile -NonInteractive -ExecutionPolicy Bypass' +
        ' -File "' + ($backendCore -replace '"', '\"') + '"' +
        ' -BackendOnly -SkipDependencies -SkipSecondaryModelDownloads -SkipSlowModeSetup' +
        ' -InstallRoot "' + ($backendRoot -replace '"', '\"') + '"'
    # The repair task must be allowed to finish while the compatibility API
    # remains alive. Calling the backend script directly tied its lifetime to
    # AutoHeal and left the task permanently running.
    $backendProcess = Start-Process -FilePath $WindowsPowerShell `
        -ArgumentList $backendArgumentLine `
        -WorkingDirectory $backendRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $backendLogRoot 'backend-startup.stdout.log') `
        -RedirectStandardError (Join-Path $backendLogRoot 'backend-startup.stderr.log') `
        -PassThru
    [IO.File]::WriteAllText(
        (Join-Path $backendRoot 'backend.pid'),
        [string]$backendProcess.Id,
        [Text.UTF8Encoding]::new($false)
    )
    $deadline = (Get-Date).AddSeconds(120)
    do {
        try {
            $status = Invoke-RestMethod -Uri 'http://127.0.0.1:47885/ui/status' -TimeoutSec 4
            $backendOk = [bool]$status.ok
        } catch { $backendOk = $false }
        if (-not $backendOk) { Start-Sleep -Milliseconds 750 }
    } while (-not $backendOk -and (Get-Date) -lt $deadline)
    # The native MSI owns shortcuts. Remove compatibility shortcuts so only
    # one Mike is visible to the user.
    @('Desktop','Programs','Startup') | ForEach-Object {
        $specialFolder = [Environment]::GetFolderPath($_)
        if ($specialFolder) { Join-Path $specialFolder 'Mike IA Local - Minike.lnk' }
    } | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Remove-Item -Force -ErrorAction SilentlyContinue
}
$results.Add((Result 'Backend completo de ferramentas' $backendOk ($(if($backendOk){'rotas HTTP 47885 disponíveis para a ponte nativa'}else{'backend de compatibilidade indisponível'}))))

$apps = @('desktop\Mike.Desktop.exe','service\Mike.Service.exe','tray\Mike.Tray.exe','cli\Mike.Cli.exe')
$appsOk = @($apps | Where-Object { -not (Test-Path (Join-Path $InstallRoot $_)) }).Count -eq 0
$results.Add((Result 'Mike Windows (Desktop/Service/Tray/CLI)' $appsOk ($(if($appsOk){'executáveis instalados'}else{"payload ausente em $InstallRoot"}))))

$results | Format-Table -AutoSize
$failed = @($results | Where-Object { -not $_.ok })
$coreNames = @('WebView2 Evergreen Runtime','Ollama runtime completo','Três perfis de modelos locais','Inferência local real','Backend completo de ferramentas','Mike Windows (Desktop/Service/Tray/CLI)')
$failedCore = @($failed | Where-Object { $_.prerequisite -in $coreNames })
if ($PostInstall -and $failed.Count) {
    try {
        $failed | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $InstallRoot 'prerequisites-failed.json') -Encoding UTF8
    } catch {
        $fallbackReport = Join-Path $env:LOCALAPPDATA 'MikeLocal\prerequisites-failed.json'
        New-Item -ItemType Directory -Path (Split-Path -Parent $fallbackReport) -Force | Out-Null
        $failed | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $fallbackReport -Encoding UTF8
        Write-Warning "Relatorio salvo sem elevacao em $fallbackReport"
    }
}
if ($PostInstall -and $failedCore.Count) {
    throw "Núcleo da Mike não ficou funcional: $($failedCore.prerequisite -join ', ')"
}
if ($PostInstall) {
    if (-not $failed.Count) { Remove-Item -LiteralPath (Join-Path $InstallRoot 'prerequisites-failed.json') -Force -ErrorAction SilentlyContinue }
    Write-Host ($(if($failed.Count){"POS-INSTALACAO FUNCIONAL: $($failed.Count) recurso(s) seguirao para auto-reparo."}else{'POS-INSTALACAO OK: todos os pre-requisitos foram verificados.'})) -ForegroundColor Green
}
