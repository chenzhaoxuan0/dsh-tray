param(
    [switch]$SelfContained
)
# 构建 dsh-tray（默认框架依赖单文件；-SelfContained 打包完整 .NET 运行时）
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\DshTray\DshTray.csproj"
$sc = if ($SelfContained) { "true" } else { "false" }

dotnet publish $proj -c Release -r win-x64 --self-contained $sc -t:Rebuild
if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED (dotnet exit=$LASTEXITCODE)" -ForegroundColor Red
    exit 1
}

$out = Join-Path $root "src\DshTray\bin\Release\net10.0-windows\win-x64\publish\DshTray.exe"
if (Test-Path $out) {
    Write-Host "OK -> $out" -ForegroundColor Green
} else {
    Write-Host "BUILD FAILED" -ForegroundColor Red
    exit 1
}
