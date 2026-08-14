param(
    [string]$Profile = "web",
    [switch]$SelfContained
)
# dsh-tray 一键安装：构建托盘程序 + 构建/注册 Web 设置插件到 dsh profile。
# 用法: powershell -ExecutionPolicy Bypass -File scripts\install.ps1
# 前置: .NET SDK 10（托盘）、Node.js 18+ 与 pnpm（插件）、已安装/可运行 dsh
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Write-Host "== [1/4] 构建托盘程序 (DshTray.exe) ==" -ForegroundColor Cyan
& (Join-Path $root "scripts\build.ps1") -SelfContained:$SelfContained
$publish = Join-Path $root "src\DshTray\bin\Release\net10.0-windows\win-x64\publish\DshTray.exe"
if (-not (Test-Path $publish)) { throw "托盘构建产物缺失: $publish" }

# 安装到 %LOCALAPPDATA%\dsh-tray（插件自动检测的第一候选位置）
$installDir = Join-Path $env:LOCALAPPDATA "dsh-tray"
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item $publish (Join-Path $installDir "DshTray.exe") -Force
Write-Host "托盘已安装: $(Join-Path $installDir 'DshTray.exe')" -ForegroundColor Green

Write-Host "== [2/4] 构建设置插件 (dsh-tray-plugin) ==" -ForegroundColor Cyan
pnpm install --dir (Join-Path $root "plugin") | Out-Null
pnpm --dir (Join-Path $root "plugin") build
if (-not (Test-Path (Join-Path $root "plugin\lib\client.js"))) { throw "插件构建产物缺失: plugin\lib" }

Write-Host "== [3/4] 注册插件到 profile '$Profile' ==" -ForegroundColor Cyan
$profileDir = Join-Path $env:USERPROFILE ".dsh\profiles\$Profile"
if (-not (Test-Path (Join-Path $profileDir "package.json"))) {
    throw "profile '$Profile' 不存在: $profileDir （请先用 dsh 启动过该 profile）"
}
$profilePkg = Join-Path $profileDir "package.json"
$json = Get-Content $profilePkg -Raw | ConvertFrom-Json
$linkPath = ($root -replace '\\', '/')
$json.dependencies | Add-Member -NotePropertyName "dsh-tray-plugin" -NotePropertyValue "link:$linkPath/plugin" -Force
if ($json.dsh.profile.bundles -notcontains "dsh-tray-plugin") {
    $json.dsh.profile.bundles += "dsh-tray-plugin"
}
$json | ConvertTo-Json -Depth 10 | Set-Content $profilePkg -Encoding UTF8
pnpm install --dir $profileDir
Write-Host "插件已注册到 profile: $profileDir" -ForegroundColor Green

Write-Host "== [4/4] 完成 ==" -ForegroundColor Cyan
Write-Host @"

安装完成。最后一步：重启 dsh 服务使插件生效（托盘右键 -> 重启，或手动重启 dsh web），
然后浏览器硬刷新，进入 设置 -> 插件 即可看到「dsh-tray · 托盘与进程控制」卡片。

- 托盘程序: $(Join-Path $installDir 'DshTray.exe')
- Web 插件: 设置 -> 插件 -> dsh-tray 卡片（显示托盘图标 / 重启 / 退出）
- 托盘程序路径解析: 设置项 trayPath -> 环境变量 DSH_TRAY_PATH -> %LOCALAPPDATA%\dsh-tray\DshTray.exe -> 常见安装位置
"@
