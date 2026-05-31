#requires -Version 5.1
<#
.SYNOPSIS
    BGIJSTool 编译脚本 - 单文件 EXE（框架依赖，不打包 .NET 运行时）

.DESCRIPTION
    编译 BGIJSTool 为单文件 EXE。目标机器需预装 .NET 运行时（约 50MB）。
    如需打包运行时（独立部署），使用 -SelfContained 参数。

.PARAMETER SelfContained
    打包 .NET 运行时到 EXE 中（文件约 150MB+，目标机器无需安装 .NET）。

.PARAMETER RuntimeIdentifier
    目标运行时标识，默认 win-x64。可选 win-x86, win-arm64。

.PARAMETER Configuration
    编译配置，默认 Release。

.EXAMPLE
    .\build.ps1
    # 框架依赖单文件（推荐，体积小）

.EXAMPLE
    .\build.ps1 -SelfContained
    # 自包含单文件（体积大，无需依赖）

.EXAMPLE
    .\build.ps1 -RuntimeIdentifier win-x86
    # 32 位单文件
#>

[CmdletBinding()]
param(
    [switch]$SelfContained,
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

# ── 路径定义 ──
$ScriptDir  = $PSScriptRoot
if (-not $ScriptDir) { $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition }

# 自动探测 .csproj 文件（兼容不同目录结构）
function Find-ProjectFile {
    param([string]$BaseDir)

    # 1. 先找当前目录下的 .csproj
    $localProj = Get-ChildItem -Path $BaseDir -Filter "*.csproj" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($localProj) { return $localProj.FullName }

    # 2. 再找 src 子目录下的 .csproj
    $srcDir = Join-Path $BaseDir "src"
    if (Test-Path $srcDir) {
        $srcProj = Get-ChildItem -Path $srcDir -Filter "*.csproj" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($srcProj) { return $srcProj.FullName }
    }

    # 3. 都没找到返回空
    return $null
}

$ProjectFile = Find-ProjectFile -BaseDir $ScriptDir
$PublishDir  = Join-Path $ScriptDir "publish"

# ── 颜色输出函数 ──
function Write-Header($text) {
    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host $text -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
}
function Write-Ok($text)  { Write-Host "[OK] $text" -ForegroundColor Green }
function Write-Warn($text){ Write-Host "[!] $text" -ForegroundColor Yellow }
function Write-Err($text) { Write-Host "[X] $text" -ForegroundColor Red }

# ── 前置检查 ──
Write-Header "BGIJSTool Build Script"

# 检查 dotnet
$dotnet = Get-Command "dotnet" -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Err "dotnet SDK 未找到，请先安装 .NET SDK"
    exit 1
}

$SdkVersion = (& dotnet --version) 2>$null
Write-Host "SDK 版本 : $SdkVersion"
Write-Host "脚本目录 : $ScriptDir"
Write-Host "项目文件 : $(if ($ProjectFile) { $ProjectFile } else { '(未找到!)' })"
Write-Host "输出目录 : $PublishDir"
Write-Host "目标 RID : $RuntimeIdentifier"
Write-Host "配置     : $Configuration"
Write-Host "部署模式 : $(if ($SelfContained) { '自包含 (Self-Contained)' } else { '框架依赖 (Framework-Dependent)' })"

# 检查项目文件
if (-not $ProjectFile) {
    Write-Err "未找到 .csproj 项目文件！"
    Write-Host ""
    Write-Host "请确认以下之一:" -ForegroundColor Yellow
    Write-Host "  1. 将 .csproj 放在脚本同级目录" -ForegroundColor Yellow
    Write-Host "  2. 将 .csproj 放在 src\ 子目录下" -ForegroundColor Yellow
    Write-Host "  3. 手动修改脚本中的 `$ProjectFile 变量" -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path $ProjectFile)) {
    Write-Err "项目文件不存在: $ProjectFile"
    exit 1
}

# ── 清理旧输出 ──
Write-Header "Step 1/4 - 清理"
if (Test-Path $PublishDir) {
    Remove-Item $PublishDir -Recurse -Force
    Write-Ok "已删除旧发布目录"
}
dotnet clean "$ProjectFile" -c $Configuration --nologo -v q | Out-Null
Write-Ok "清理完成"

# ── 还原依赖 ──
Write-Header "Step 2/4 - 还原 NuGet 包"
dotnet restore "$ProjectFile" --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Err "还原失败"
    exit $LASTEXITCODE
}
Write-Ok "还原完成"

# ── 编译参数 ──
Write-Header "Step 3/4 - 编译并发布"

dotnet build "$ProjectFile" -c $Configuration --no-restore --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Err "编译失败!"
    exit $LASTEXITCODE
}
Write-Ok "Release 编译完成"

$TestProjects = Get-ChildItem -Path $ScriptDir -Recurse -Filter "*.Tests.csproj" -ErrorAction SilentlyContinue
if ($TestProjects.Count -gt 0) {
    foreach ($TestProject in $TestProjects) {
        dotnet test $TestProject.FullName -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) {
            Write-Err "测试失败: $($TestProject.FullName)"
            exit $LASTEXITCODE
        }
    }
    Write-Ok "测试完成"
} else {
    Write-Warn "未发现测试项目，跳过 dotnet test"
}

$PublishArgs = @(
    "publish", "$ProjectFile"
    "-c", $Configuration
    "-r", $RuntimeIdentifier
    "-p:PublishSingleFile=true"
    "-p:SelfContained=$($SelfContained.IsPresent)"
    "-p:PublishReadyToRun=$(if ($SelfContained) { 'true' } else { 'false' })"
    "-p:IncludeNativeLibrariesForSelfExtract=true"
    "-p:EnableCompressionInSingleFile=$(if ($SelfContained) { 'true' } else { 'false' })"
    "-p:DebugType=none"
    "-p:DebugSymbols=false"
    "-p:TrimUnusedCode=$(if ($SelfContained) { 'true' } else { 'false' })"
    "--self-contained", "$($SelfContained.IsPresent)"
    "-o", "$PublishDir"
    "--nologo"
)

# 执行发布
& dotnet @PublishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Err "发布失败!"
    exit $LASTEXITCODE
}
Write-Ok "发布完成"

# ── 结果输出 ──
Write-Header "Step 4/4 - 发布结果"

$ExePath = Join-Path $PublishDir "BGIJSTool.exe"
if (Test-Path $ExePath) {
    $ExeInfo = Get-Item $ExePath
    $SizeMB  = [math]::Round($ExeInfo.Length / 1MB, 2)
    Write-Ok "主程序  : $ExePath"
    Write-Host "文件大小: $SizeMB MB" -ForegroundColor Green

    # 计算 SHA256
    $Hash = (Get-FileHash $ExePath -Algorithm SHA256).Hash
    Write-Host "SHA256  : $Hash" -ForegroundColor DarkGray
} else {
    Write-Warn "未找到 BGIJSTool.exe"
}

# 列出所有输出文件
Write-Host "`n输出文件清单:" -ForegroundColor Cyan
$Files = Get-ChildItem $PublishDir -Recurse -File | Select-Object Name,
    @{N="Size"; E={ if ($_.Length -gt 1MB) { "{0:N2} MB" -f ($_.Length/1MB) } else { "{0:N0} KB" -f ($_.Length/1KB) } }},
    @{N="Type"; E={ if ($_.Extension -eq ".exe") { "可执行" } elseif ($_.Extension -eq ".dll") { "动态库" } elseif ($_.Extension -eq ".pdb") { "调试符号" } else { "其他" } }}

$Files | Format-Table -AutoSize

# 单文件验证
$DllCount = ($Files | Where-Object { $_.Type -eq "动态库" }).Count
if ($DllCount -eq 0 -and (Test-Path $ExePath)) {
    Write-Ok "验证通过: 单文件发布成功（无外部 DLL 依赖）"
} elseif ($DllCount -gt 0) {
    Write-Warn "检测到 $DllCount 个 DLL 文件，单文件提取模式可能未生效"
}

# ── 复制辅助文件 ──
Write-Header "Step 5/5 - 复制辅助文件"

$CopySource = Join-Path $ScriptDir "copy"
$ConfigSource = Join-Path $ScriptDir "config.json"
$ReadmeSource = Join-Path $ScriptDir "README.md"
$SpecSource = Join-Path $ScriptDir "SPEC.md"

if (Test-Path $CopySource) {
    $CopyDest = Join-Path $PublishDir "copy"
    robocopy $CopySource $CopyDest /E /NFL /NDL | Out-Null
    Write-Ok "已复制 copy 文件夹"
} else {
    Write-Warn "未找到 copy 文件夹"
}

if (Test-Path $ConfigSource) {
    Copy-Item $ConfigSource (Join-Path $PublishDir "config.json") -Force
    Write-Ok "已复制 config.json"
} else {
    Write-Warn "未找到 config.json"
}

if (Test-Path $ReadmeSource) {
    Copy-Item $ReadmeSource (Join-Path $PublishDir "README.md") -Force
    Write-Ok "已复制 README.md"
} else {
    Write-Warn "未找到 README.md"
}

if (Test-Path $SpecSource) {
    Copy-Item $SpecSource (Join-Path $PublishDir "SPEC.md") -Force
    Write-Ok "已复制 SPEC.md"
} else {
    Write-Warn "未找到 SPEC.md"
}

# ── 使用说明 ──
Write-Host "`n========================================" -ForegroundColor Green
Write-Host "编译完成!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green

if (-not $SelfContained) {
    Write-Host "`n注意: 此为框架依赖部署 (FDD)" -ForegroundColor Yellow
    Write-Host "      目标机器必须安装 .NET 运行时才能运行此程序" -ForegroundColor Yellow
    Write-Host "      如需独立运行，请使用: .\build.ps1 -SelfContained" -ForegroundColor Yellow
}

Write-Ok "编译流程完成"
