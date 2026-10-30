# PIKAN 一键出包：编译 → 发布 → 签名
#
# 两种形态（默认出单文件，这是拿去分发的形态）：
#
#   单文件  release-portable\PIKAN.exe
#                            一个文件走天下。体积靠 EnableCompressionInSingleFile 压掉一大半
#                            （156 MB 的多文件目录 → 约 45 MB 的单个 exe）。
#                            早年放弃单文件是因为它要把 WPF 的 native 组件自解压到 %TEMP%，
#                            被安全软件拦住就是「双击毫无反应」；实测 .NET 10 已经不再落地
#                            那个解压目录（直接从 bundle 里加载），这条路重新可用了。
#
#   便携目录 portable\       多文件目录。-Portable 切过来，调试 / 出现问题时对照用。
#
# 用法（PowerShell 7）：
#   pwsh .\Publish.ps1                  # 出单文件 release-portable\PIKAN.exe 并签名
#   pwsh .\Publish.ps1 -NoSign          # 只发布，不签名（本机快速验证）
#   pwsh .\Publish.ps1 -Portable        # 出便携目录形态
#   pwsh .\Publish.ps1 -Out D:\PIKAN    # 输出到别处（直接指定 U 盘路径）
#
# 注意 -Out 目录会被清空重建：它必须是发布产物目录（release-portable\ / portable\ / 空目录），
# 别指向放着自己东西的文件夹。
#
# ★ PublishTrimmed 别再试了（2026-10-30 实测），详见 PIKAN.csproj 里的注释。
#   结论：WPF 的 XAML 引擎靠反射创建 TemplateContentLoader，trimmer 看不见，
#    MainWindow.InitializeComponent() 直接 MissingMethodException，连窗口都出不来。

param(
    [string] $Out,
    [switch] $Portable,
    [switch] $NoSign
)

$ErrorActionPreference = 'Stop'

if (-not $Out) {
    $Out = Join-Path $PSScriptRoot ($Portable ? 'portable' : 'release-portable')
}

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
Write-Host "形态:   $(if ($Portable) { '便携目录（多文件）' } else { '单文件 exe' })" -ForegroundColor DarkGray

Set-Location $PSScriptRoot

# NuGet restore 在部分受管控环境里会抛 “Value cannot be null (Parameter 'path1')”，
# 与本工程代码无关。这里做一次兜底：正常跑一遍，失败就用 --no-restore 再跑
# （obj\project.assets.json 已在，缺的只是联网还原）。
function Invoke-Dotnet {
    param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $Rest)

    & $dotnet.Source @Rest
    $code = $LASTEXITCODE

    if ($null -eq $code -or $code -ne 0) {
        Write-Host "  （首次尝试未成功，改用 --no-restore 重试）" -ForegroundColor Yellow
        & $dotnet.Source @Rest '--no-restore'
        $code = $LASTEXITCODE
    }

    if ($null -eq $code -or $code -ne 0) {
        throw "dotnet $($Rest -join ' ') 失败（exit $code）"
    }
}

# 注意：参数必须以数组形式 splat 进来（Invoke-Dotnet @buildArgs，而不是把 -c / -p:x=y
# 直接写在命令行里）——后者会被 shell 当成本函数的参数名去解析，
# -o / -p 恰好都能对上 -OutVariable / -OutBuffer / -ProgressAction 等通用参数，报 “ambiguous”。
$buildArgs = @('build', '-c', 'Release')

$publishArgs = @(
    'publish', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:DebugType=none', '-p:SatelliteResourceLanguages=en',
    '--output', $Out
)

if ($Portable) {
    $publishArgs += @('-p:PublishSingleFile=false')
}
else {
    # 三条一起才是「一个文件也能跑」的全部条件：
    #   PublishSingleFile                     打包成一个 exe
    #   IncludeNativeLibrariesForSelfExtract  native 库（wpfgfx / d3dcompiler 等）进 bundle
    #   EnableCompressionInSingleFile         启用压缩，体积再减一半
    $publishArgs += @(
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true'
    )
}

Write-Host "`n[1/4] 编译 Release ..." -ForegroundColor Cyan
Invoke-Dotnet @buildArgs

Write-Host "`n[2/4] 清空并重建 $Out ..." -ForegroundColor Cyan
if (Test-Path $Out) {
    Remove-Item (Join-Path $Out '*') -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    New-Item -ItemType Directory -Path $Out -Force | Out-Null
}

Write-Host "`n[3/4] 发布 → $Out" -ForegroundColor Cyan
Invoke-Dotnet @publishArgs

$exe = Join-Path $Out 'PIKAN.exe'
if (-not (Test-Path $exe)) { throw "发布完成但找不到 $exe" }

# 体积报告：单文件看 exe 本身；便携版才是整个目录
$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
$files = Get-ChildItem $Out -File

if ($Portable) {
    $dirMb = [math]::Round((($files | Measure-Object Length -Sum).Sum) / 1MB, 1)
    Write-Host "  PIKAN.exe $sizeMb MB；整个目录 $dirMb MB · $($files.Count) 个文件" -ForegroundColor DarkGray
}
else {
    Write-Host "  单文件 PIKAN.exe $sizeMb MB（目录内 $($files.Count) 个文件）" -ForegroundColor DarkGray
    if ($files.Count -ne 1) {
        Write-Host "  !! 期望只剩一个文件，实际还有 $($files.Count - 1) 个附属文件" -ForegroundColor Yellow
        $files | Where-Object { $_.Name -ne 'PIKAN.exe' } | ForEach-Object {
            Write-Host "     · $($_.Name)  $([math]::Round($_.Length / 1KB, 1)) KB" -ForegroundColor DarkGray
        }
    }
}

# 低完整性标签清理：
# 某些受管控的工作目录（例如 agent 工作区）整棵树都带着
# Mandatory Label\Low Mandatory Level 的继承 ACE，从这里发布的 exe 启动后
# 会被降成低完整性进程——写不了 HKCU（图形首选项申请失败）、临时目录也受限。
# 拷到别处使用不受影响，但直接在本目录双击就会中招，这里顺手把标签拉回 Medium。
Write-Host "`n[3.5/4] 完整性标签检查 ..." -ForegroundColor Cyan
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
    Write-Host "`n[4/4] 跳过签名（-NoSign）" -ForegroundColor Yellow
}
else {
    Write-Host "`n[4/4] 签名 ..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'Sign.ps1') -Exe $exe
    if ($LASTEXITCODE -ne 0) { throw "签名失败（exit $LASTEXITCODE）" }
}

if ($Portable) {
    Write-Host "`n完成：双击 $exe 即可运行；整个目录可直接拷到任意 Win64 机器。" -ForegroundColor Green
}
else {
    Write-Host "`n完成：$exe 是唯一要分发的文件，拷到任意 Win64 机器双击即可。" -ForegroundColor Green
}
