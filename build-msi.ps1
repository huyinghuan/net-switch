[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$appProject = Join-Path $projectRoot "NetSwitcher.csproj"
$installerProject = Join-Path $projectRoot "installer\NetSwitcher.Installer.wixproj"
$publishDir = Join-Path $projectRoot "artifacts\publish\framework-dependent"
$outputDir = Join-Path $projectRoot "artifacts"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "未找到 .NET SDK。请先安装 .NET 8 或更高版本的 SDK。"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content -LiteralPath $appProject -Raw
    $versionNode = $projectXml.SelectSingleNode("/Project/PropertyGroup/Version")
    if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        throw "无法从 NetSwitcher.csproj 读取 Version。"
    }
    $Version = $versionNode.InnerText.Trim()
}

if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "版本号必须采用 1.2.3 或 1.2.3.4 格式。当前值：$Version"
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
Get-ChildItem -LiteralPath $outputDir -Filter "NetSwitcher-Setup-*-x64.msi" -File |
    Remove-Item -Force

Write-Host "[1/2] 发布框架依赖单文件应用..." -ForegroundColor Cyan
& dotnet publish $appProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "应用发布失败，退出代码：$LASTEXITCODE"
}

Write-Host "[2/2] 构建 MSI 安装包..." -ForegroundColor Cyan
& dotnet build $installerProject `
    -c $Configuration `
    "-p:ProductVersion=$Version" `
    "-p:PublishDir=$publishDir"
if ($LASTEXITCODE -ne 0) {
    throw "MSI 构建失败，退出代码：$LASTEXITCODE"
}

$msiPath = Join-Path $outputDir "NetSwitcher-Setup-$Version-x64.msi"
if (-not (Test-Path -LiteralPath $msiPath -PathType Leaf)) {
    throw "构建完成但未找到 MSI：$msiPath"
}

$sizeMb = [Math]::Round((Get-Item -LiteralPath $msiPath).Length / 1MB, 2)
Remove-Item -LiteralPath $publishDir -Recurse -Force
$publishRoot = Split-Path -Parent $publishDir
if ((Test-Path -LiteralPath $publishRoot) -and
    -not (Get-ChildItem -LiteralPath $publishRoot -Force | Select-Object -First 1)) {
    Remove-Item -LiteralPath $publishRoot -Force
}
Write-Host ""
Write-Host "构建成功：$msiPath" -ForegroundColor Green
Write-Host "文件大小：$sizeMb MB"
Write-Host "此 MSI 需要目标电脑预先安装 .NET 8 Desktop Runtime (x64)。"
