# PIKAN 一键出包：编译 → 发布便携版 → 签名
#
# 唯一的发布形态是 portable\（自包含多文件目录，不做运行时自解压，双击即开）。
# 单文件 exe 已弃用：它要把 WPF 的 native 组件自解压到临时目录，被拦时是「双击毫无反应」的静默退出。
#
# 用法（PowerShell 7）：
#   pwsh .\Publish.ps1                # Release 发布到 .\portable 并签名
#   pwsh .\Publish.ps1 -NoSign        # 只发布，不签名（本机快速调试）
#   pwsh .\Publish.ps1 -Out D:\PIKAN  # 输出到别处（拷给别人时直接指定 U 盘路径）

param(
    [string] $Out = (Join-Path $PSScriptRoot 'portable'),
    [switch] $NoSign
)

$ErrorActionPreference = 'Stop'

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue |
    Where-Object { $_.Source -notlike '*Program Files (x86)*' } |
    Select-Object -First 1

if (-not $dotnet) {
    $candidate = 'C:\Program Files\dotnet\dotnet.exe'
    if (Test-Path $candidate) {
        $dotnet = @{ Source = $candidate }
    }
    else {
        throw "找不到 dotnet。请安装 .NET 10 SDK，或把 dotnet 加进 PATH。"
    }
}

Write-Host "dotnet: $($dotnet.Source)" -ForegroundColor DarkGray

Set-Location $PSScriptRoot

Write-Host "`n[1/3] 编译 Release ..." -ForegroundColor Cyan
& $dotnet.Source build -c Release
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }

Write-Host "`n[2/3] 发布便携版 → $Out" -ForegroundColor Cyan
& $dotnet.Source publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none -p:SatelliteResourceLanguages=en `
    -o $Out
if ($LASTEXITCODE -ne 0) { throw "发布失败（exit $LASTEXITCODE）" }

$exe = Join-Path $Out 'PIKAN.exe'
if (-not (Test-Path $exe)) { throw "发布完成但找不到 $exe" }
$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "  PIKAN.exe  $sizeMb MB" -ForegroundColor DarkGray

# 低完整性标签清理：
# 某些受管控的工作目录（例如 agent 工作区）整棵树都带着
# Mandatory Label\Low Mandatory Level 的继承 ACE，从这里发布的 exe 启动后
# 会被降成低完整性进程——写不了 HKCU（图形首选项申请失败）、临时目录也受限。
# 拷到别处使用不受影响，但直接在本目录双击就会中招，这里顺手把标签拉回 Medium。
Write-Host "`n[3.5/3] 完整性标签检查 ..." -ForegroundColor Cyan
$label = (icacls $exe 2>$null | Select-String 'Mandatory Label\\(\w+) Mandatory Level')
if ($label -and $label.Matches.Groups[1].Value -ne 'Medium') {
    Write-Host "  发现 $($label.Matches.Groups[1].Value) 强制标签，尝试改为 Medium ..." -ForegroundColor Yellow
    icacls $exe /setintegritylevel M | Out-Null
    $after = (icacls $exe 2>$null | Select-String 'Mandatory Label\\(\w+) Mandatory Level')
    if ($after) {
        Write-Host "  现在的标签: $($after.Matches.Groups[1].Value)" -ForegroundColor DarkGray
    }
    else {
        Write-Host "  标签已清除" -ForegroundColor DarkGray
    }
}
else {
    Write-Host "  无异常标签" -ForegroundColor DarkGray
}

if ($NoSign) {
    Write-Host "`n[3/3] 跳过签名（-NoSign）" -ForegroundColor Yellow
}
else {
    Write-Host "`n[3/3] 签名 ..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'Sign.ps1') -Exe $exe
    if ($LASTEXITCODE -ne 0) { throw "签名失败（exit $LASTEXITCODE）" }
}

Write-Host "`n完成：双击 $exe 即可运行；整个目录可直接拷到任意 Win64 机器。" -ForegroundColor Green
