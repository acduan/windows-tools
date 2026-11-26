#Requires -Version 5.1
<#
.SYNOPSIS
    编译 WarmOverlay（自定义夜间模式 / 护眼叠加）。

.DESCRIPTION
    使用系统自带的 .NET Framework C# 编译器编译，无需安装 Visual Studio 或 .NET SDK。
    编译产物默认输出到当前目录的 WarmOverlay.exe。

.PARAMETER Output
    输出 exe 路径。默认为脚本同目录下的 WarmOverlay.exe。

.PARAMETER Install
    编译成功后，停止正在运行的 WarmOverlay 进程并用新版本替换到 -InstallDir。

.PARAMETER InstallDir
    部署目录。配合 -Install 使用。默认为脚本所在目录。

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -Install -InstallDir 'E:\Tool\自定义夜间模式'
#>
[CmdletBinding()]
param(
    [string]$Output,
    [switch]$Install,
    [string]$InstallDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $scriptDir 'WarmOverlayUI.cs'

if (-not (Test-Path -LiteralPath $source)) {
    throw "找不到源码：$source"
}
if (-not $Output) {
    $Output = Join-Path $scriptDir 'WarmOverlay.exe'
}

# ---- 定位 C# 编译器（优先 64 位）----
$candidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $csc) {
    $msg = '未找到 .NET Framework C# 编译器 (csc.exe)。' + [Environment]::NewLine +
           '本工具依赖 Windows 自带的 .NET Framework 4.x 运行时。' + [Environment]::NewLine +
           '请在「启用或关闭 Windows 功能」中确认 .NET Framework 4.8 已启用。'
    throw $msg
}

Write-Host "编译器: $csc" -ForegroundColor DarkGray
Write-Host "源码  : $source" -ForegroundColor DarkGray
Write-Host "输出  : $Output" -ForegroundColor DarkGray

# ---- 编译 ----
$refs = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll'
) | ForEach-Object { "/r:$_" }

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/optimize+'
    '/platform:anycpu'
    "/out:$Output"
) + $refs + @($source)

& $csc @cscArgs
$code = $LASTEXITCODE

if ($code -ne 0) {
    throw "编译失败，csc 退出码 $code"
}
if (-not (Test-Path -LiteralPath $Output)) {
    throw "编译未生成输出文件：$Output"
}

$exe = Get-Item -LiteralPath $Output
Write-Host ("编译成功: {0} ({1:N0} 字节)" -f $exe.Name, $exe.Length) -ForegroundColor Green

# ---- 可选：部署 ----
if ($Install) {
    if (-not $InstallDir) { $InstallDir = $scriptDir }
    if (-not (Test-Path -LiteralPath $InstallDir)) {
        throw "部署目录不存在：$InstallDir"
    }
    $target = Join-Path $InstallDir 'WarmOverlay.exe'

    # 目标与输出相同则无需部署
    if ([System.IO.Path]::GetFullPath($target) -eq [System.IO.Path]::GetFullPath($Output)) {
        Write-Host "输出已在部署目录，跳过部署。" -ForegroundColor Yellow
    }
    else {
        Get-Process -Name 'WarmOverlay' -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Host "停止进程 PID $($_.Id)" -ForegroundColor DarkGray
            $_ | Stop-Process -Force
        }
        Start-Sleep -Milliseconds 900
        Copy-Item -LiteralPath $Output -Destination $target -Force
        Write-Host "已部署到: $target" -ForegroundColor Green
        Write-Host "提示：程序被强制结束时显示器会残留暖色，下次启动会自动还原。" -ForegroundColor DarkGray
    }
}
