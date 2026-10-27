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

if ($NoSign) {
    Write-Host "`n[3/3] 跳过签名（-NoSign）" -ForegroundColor Yellow
}
else {
    Write-Host "`n[3/3] 签名 ..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'Sign.ps1') -Exe $exe
    if ($LASTEXITCODE -ne 0) { throw "签名失败（exit $LASTEXITCODE）" }
}

Write-Host "`n完成：双击 $exe 即可运行；整个目录可直接拷到任意 Win64 机器。" -ForegroundColor Green
