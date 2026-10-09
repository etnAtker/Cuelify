#Requires -Version 7.0
param(
    [string]$ReleasePath = 'artifacts/publish/win-x64',
    [string]$ReportPath = 'artifacts/validation/release.json'
)
$ErrorActionPreference = 'Stop'
$releaseRoot = (Resolve-Path -LiteralPath $ReleasePath).Path
$requiredAssets = @('Cuelify.Desktop.exe', 'coreclr.dll', 'hostfxr.dll', 'Assets/silero_vad.onnx',
    'onnxruntime.dll', 'libSkiaSharp.dll', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses/packages.json',
    'runtimes/win-x64/native/vulkan/llama.dll', 'runtimes/win-x64/native/vulkan/ggml-vulkan.dll')
foreach ($asset in $requiredAssets) {
    if (!(Test-Path -LiteralPath (Join-Path $releaseRoot $asset))) { throw "缺少发行资产：$asset" }
}
if ((Get-ChildItem -LiteralPath (Join-Path $releaseRoot 'runtimes') -Directory | Where-Object Name -NE 'win-x64')) {
    throw '发行包包含非 win-x64 原生资产。'
}
if ((Get-ChildItem -LiteralPath $releaseRoot -File -Recurse | Where-Object {
    $_.Extension -eq '.gguf' -or $_.Name -match 'Cuelify\..*Tests|xunit|testhost|settings\.json'
})) { throw '发行包包含模型、测试程序集或用户设置。' }
$vadHash = (Get-FileHash -LiteralPath (Join-Path $releaseRoot 'Assets/silero_vad.onnx') -Algorithm SHA256).Hash
if ($vadHash -ne '1A153A22F4509E292A94E67D6F9B85E8DEB25B4988682B7E174C65279D8788E3') { throw 'Silero 发行资产哈希不符。' }

# 只测试包内运行时启动/退出；不调用真实服务，不代替独立无 SDK 机器验收。
$startInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $releaseRoot 'Cuelify.Desktop.exe'))
$startInfo.UseShellExecute = $false
$startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$startInfo.Environment['DOTNET_ROOT'] = Join-Path $releaseRoot '不存在的全局运行时'
foreach ($credentialName in @('ELEVENLABS_API_KEY', 'TRANSLATION_API_KEY', 'DEEPSEEK_API_KEY')) {
    $startInfo.Environment.Remove($credentialName) | Out-Null
}
$applicationProcess = [Diagnostics.Process]::Start($startInfo)
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        $applicationProcess.Refresh()
    } while (!$applicationProcess.HasExited -and (!$applicationProcess.MainWindowHandle -or !$applicationProcess.MainWindowTitle.StartsWith('Cuelify')) -and [DateTime]::UtcNow -lt $deadline)
    if ($applicationProcess.HasExited -or !$applicationProcess.MainWindowHandle -or !$applicationProcess.MainWindowTitle.StartsWith('Cuelify')) { throw 'Cuelify 原生主窗口未在 20 秒内创建。' }
    if (!$applicationProcess.WaitForInputIdle(20000)) { throw '窗口消息循环未在 20 秒内就绪。' }
    $applicationProcess.Refresh()
    $runtimePath = ($applicationProcess.Modules | Where-Object ModuleName -EQ 'coreclr.dll' | Select-Object -First 1).FileName
    if ($runtimePath -ne (Join-Path $releaseRoot 'coreclr.dll')) { throw '未使用发行包中的 .NET 运行时。' }
    $closeRequested = $applicationProcess.CloseMainWindow()
    if (!$closeRequested) { throw '系统未接受窗口关闭请求。' }
    if (!$applicationProcess.WaitForExit(20000)) { throw '窗口正常关闭超时。' }
    if ($applicationProcess.ExitCode -ne 0) { throw "退出码：$($applicationProcess.ExitCode)" }
    $report = [ordered]@{ WindowCreated = $true; GracefulCloseRequested = $closeRequested;
        ExitCode = $applicationProcess.ExitCode; RuntimePath = $runtimePath; VadSha256 = $vadHash;
        RequiredAssets = $requiredAssets; IndependentNoSdkMachineVerified = $false }
    $reportFile = [IO.Path]::GetFullPath($ReportPath)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($reportFile)) -Force | Out-Null
    $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportFile -Encoding utf8
    '发行资产、包内运行时启动和正常退出检查通过。'
} finally {
    if (!$applicationProcess.HasExited) { $applicationProcess.Kill($true); $applicationProcess.WaitForExit() }
    $applicationProcess.Dispose()
}
