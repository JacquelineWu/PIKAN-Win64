#Requires -Version 7.0

# PIKAN 一键发版：发布 → 签名 → 打 tag → 推两个远端 → 建 Release/发行版 → 传附件 → 回下载校验
#
# 一次招呼的顺序很重要，踩过坑的地方都在注释里标了「为什么」。
#
# 用法（PowerShell 7）：
#   pwsh .\Release.ps1 -Tag v1.0.1
#   pwsh .\Release.ps1 -Tag v1.0.1 -DryRun        # 只把计划打印出来，一个字节都不动
#   pwsh .\Release.ps1 -Tag v1.0.1 -SkipPublish   # 复用已有的 release-portable\PIKAN.exe
#   pwsh .\Release.ps1 -Tag v1.0.1 -Force -Yes    # 覆盖已存在的同名 tag / Release / 附件
#   pwsh .\Release.ps1 -Tag v1.0.1 -Draft         # 发成草稿（GitHub；Gitee 无草稿概念，仅不发改名通知）
#   pwsh .\Release.ps1 -Tag v1.0.1 -SkipGitee     # 只发 GitHub
#
# Gitee 令牌的三种给法（优先级从高到低）：
#   -GiteeToken (Read-Host -AsSecureString 提示也走这条路)
#   $env:GITEE_TOKEN
#   交互式提示（Read-Host -AsSecureString）
# 令牌只在这次进程内存里存在，不落盘、不进 git、不写日志。用完建议在
# https://gitee.com/profile/personal_access_tokens 删掉，下次发版再生成一张。
#
# Release Notes 默认从 RELEASE-NOTES.md 取（`---` 分隔线之后的部分才是正文），
# 里面的 {{TAG}} {{SHA256}} {{SIZE_MB}} 等占位符会在这里换成真值。
# -AutoNotes 可以不要模板，改成从上次 tag 之后的 commit 自动生成。

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Tag,

    [string] $Title,
    [string] $Notes,
    [string] $Exe,
    [string] $Cert,
    [string] $Branch = 'main',
    [string] $GitHubRepo,
    [string] $GiteeRepo,
    [SecureString] $GiteeToken,

    [switch] $SkipPublish,
    [switch] $NoSign,
    [switch] $NoCert,
    [switch] $AutoNotes,
    [switch] $SkipVerify,
    [switch] $SkipGitHub,
    [switch] $SkipGitee,
    [switch] $Draft,
    [switch] $Force,
    [switch] $Yes,
    [switch] $AllowDirty,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
# PS 7.4+ 默认会让非零退出的原生命令直接抛错，这里关掉、统一用 $LASTEXITCODE 判断，
# 否则 git 的正常「没有这条远端分支」之类的返回会把脚本打断在不该断的地方。
$PSNativeCommandUseErrorActionPreference = $false
$ProgressPreference = 'SilentlyContinue'

$root = $PSScriptRoot
Set-Location $root

if (-not $Title) { $Title = "PIKAN $Tag（单文件版 · Win64）" }
if (-not $Notes) { $Notes = Join-Path $root 'RELEASE-NOTES.md' }
if (-not $Exe) { $Exe = Join-Path $root 'release-portable' 'PIKAN.exe' }
if (-not $Cert) { $Cert = Join-Path $root 'PIKAN-CodeSigning.cer' }
if ($NoCert) { $Cert = $null }

$totalSteps = 7

# ---------------------------------------------------------------- 小工具

function Step([string] $No, [string] $Text) {
    Write-Host "`n[$No/$totalSteps] $Text" -ForegroundColor Cyan
}

function Ok([string] $Msg) { Write-Host "  ✓ $Msg" -ForegroundColor Green }
function Info([string] $Msg) { Write-Host "  · $Msg" -ForegroundColor DarkGray }
function Warn([string] $Msg) { Write-Host "  ! $Msg" -ForegroundColor Yellow }

function Die([string] $Msg) { throw $Msg }

# 注意别把参数名叫 $Args——那是 PowerShell 的自动变量，同名会拿不到传进来的东西
function Invoke-Git([string[]] $GitArgs, [switch] $AllowFail) {
    $out = & git @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0 -and -not $AllowFail) {
        Die "git $($GitArgs -join ' ') 失败（exit $LASTEXITCODE）：`n$($out -join "`n")"
    }
    return $out
}

function Convert-SecureStringToPlain([SecureString] $Sec) {
    if (-not $Sec) { return $null }
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Sec)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

# 从远端 URL 里抠出 owner/repo，支持 https 与 ssh 两种写法
function Get-RemoteSlug([string] $Remote) {
    $url = (Invoke-Git @('remote', 'get-url', $Remote)).Trim()
    if ($url -match '^[a-z+]+://[^/]+/(.+?)/?$') { return $Matches[1] -replace '\.git$', '' }
    if ($url -match '^[^@]+@[^:]+:(.+?)/?$') { return $Matches[1] -replace '\.git$', '' }
    Die "解析不出远端 $Remote 的 owner/repo：$url"
}

function Get-FileSha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# ---------------------------------------------------------------- [1/7] 前置检查

Step '1' '前置检查'

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Die '找不到 git。' }
if ($Tag -notmatch '^v\d+\.\d+') { Warn "tag「$Tag」不像 vX.Y 形式，确认没打错？" }
if ($Force -and -not $Yes) {
    Die "-Force 会覆盖已存在的 tag / Release / 附件。确认要这么做就加 -Yes。"
}

$dirty = Invoke-Git @('status', '--porcelain')
if ($dirty -and -not $AllowDirty) {
    Die "工作区不干净（$($dirty.Count) 个改动）。先 commit 掉，或者加 -AllowDirty 强行发。"
}
if ($dirty) { Warn "工作区有 $($dirty.Count) 个未提交改动（-AllowDirty）" }
else { Ok '工作区干净' }

$curBranch = (Invoke-Git @('symbolic-ref', '--short', 'HEAD')).Trim()
if ($curBranch -ne $Branch) { Warn "当前在 $curBranch 分支，tag 会打在这里而不是 $Branch" }

$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $SkipGitHub) {
    if (-not $gh) { Die '找不到 gh（GitHub CLI）。只发 Gitee 就加 -SkipGitHub。' }
    if (-not $GitHubRepo) { $GitHubRepo = Get-RemoteSlug 'origin' }
    Info "GitHub 仓库：$GitHubRepo"
    try {
        & gh auth status 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Die 'gh 未登录，先跑 gh auth login。' }
    }
    catch { Die 'gh auth status 检查失败，先跑 gh auth login。' }
    Ok 'gh 已登录'
}

if (-not $SkipGitee) {
    if (-not $GiteeRepo) { $GiteeRepo = Get-RemoteSlug 'gitee' }
    Info "Gitee  仓库：$GiteeRepo"
}

if ($AutoNotes) {
    Info '正文来源：commit 自动生成'
}
else {
    if (-not (Test-Path -LiteralPath $Notes)) {
        Die "找不到 Release Notes 模板：$Notes（也可以加 -AutoNotes 让它从 commit 生成）"
    }
    Ok "Release Notes 模板：$(Split-Path $Notes -Leaf)"
}

if (-not $SkipPublish) { Info '将调用 Publish.ps1 重新编译 + 发布 + 签名' }
else {
    if (-not (Test-Path -LiteralPath $Exe)) { Die "-SkipPublish 但 $Exe 不存在。" }
    Info "-SkipPublish：复用已有 $Exe"
}

# 同名 tag 是否已存在
$tagExists = @{}
foreach ($r in @('origin', 'gitee')) {
    if ($r -eq 'origin' -and $SkipGitHub) { continue }
    if ($r -eq 'gitee' -and $SkipGitee) { continue }
    $remoteTag = Invoke-Git @('ls-remote', '--tags', $r, "refs/tags/$Tag") -AllowFail
    $tagExists[$r] = [bool]($remoteTag | Where-Object { $_ -match "refs/tags/$([regex]::Escape($Tag))$" })
}
$localTag = Invoke-Git @('tag', '-l', $Tag) -AllowFail
if ($localTag) { $tagExists['__local'] = $true }

foreach ($k in $tagExists.Keys) {
    if ($tagExists[$k]) {
        if (-not $Force) { Die "$k 上已经有 tag $Tag 了。换个 tag，或者加 -Force -Yes 覆盖。" }
        Warn "$k 上已有 tag $Tag，-Force 将覆盖"
    }
}

$certSuffix = if ($Cert -and (Test-Path -LiteralPath $Cert)) { " + $(Split-Path $Cert -Leaf)" } else { '' }
$plan = @(
    "tag       : $Tag → commit $((Invoke-Git @('rev-parse', '--short', 'HEAD')).Trim())",
    "exe       : $Exe",
    "标题      : $Title",
    "GitHub    : $(if ($SkipGitHub) { '跳过' } else { $GitHubRepo })",
    "Gitee     : $(if ($SkipGitee) { '跳过' } else { $GiteeRepo })",
    "附件      : PIKAN.exe$certSuffix",
    "草稿      : $([bool]$Draft)",
    "回下载校验: $(-not $SkipVerify)"
)
Write-Host "`n  ── 发版计划 ──" -ForegroundColor White
$plan | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }

if ($DryRun) {
    Write-Host "`n-DryRun：以上只是计划，没有任何副作用。" -ForegroundColor Yellow
    exit 0
}

# ---------------------------------------------------------------- [2/7] 发布

Step '2' '编译 → 发布 → 签名'

if ($SkipPublish) {
    Info '跳过（-SkipPublish）'
}
else {
    $pubArgs = @('-Out', (Split-Path $Exe -Parent))
    if ($NoSign) { $pubArgs += '-NoSign' }
    & (Join-Path $root 'Publish.ps1') @pubArgs
    if ($LASTEXITCODE -ne 0) { Die "Publish.ps1 失败（exit $LASTEXITCODE）" }
}

if (-not (Test-Path -LiteralPath $Exe)) { Die "发布完却找不到 $Exe" }

$exeItem = Get-Item -LiteralPath $Exe
$sizeMb = [math]::Round($exeItem.Length / 1MB, 1)
$sizeBytes = $exeItem.Length
Ok "PIKAN.exe  $sizeMb MB  ($sizeBytes 字节)"

if (-not $NoSign) {
    $sig = Get-AuthenticodeSignature -FilePath $Exe
    if ($sig.Status -ne 'Valid') { Warn "签名状态不是 Valid：$($sig.Status)（不影响发布，但用户会看到 SmartScreen）" }
    else { Ok "签名 Valid · $($sig.SignerCertificate.Subject -replace '^CN=', '')" }
}

$certItem = if ($Cert -and (Test-Path -LiteralPath $Cert)) { Get-Item -LiteralPath $Cert } else { $null }
if ($certItem) { Info "附带证书：$($certItem.Name)" }

# ---------------------------------------------------------------- [3/7] 正文

Step '3' '算哈希 + 生成 Release Notes'

$sha = Get-FileSha256 $Exe
Ok "SHA256 $sha"

$commitShort = (Invoke-Git @('rev-parse', '--short', 'HEAD')).Trim()

if ($AutoNotes) {
    $lastTag = (Invoke-Git @('describe', '--tags', '--abbrev=0', 'HEAD~1') -AllowFail | Select-Object -First 1)
    if ($LASTEXITCODE -ne 0 -or -not $lastTag) { $lastTag = $null }
    $range = if ($lastTag) { "$lastTag..HEAD" } else { 'HEAD' }
    $subjects = Invoke-Git @('log', $range, '--no-merges', '--pretty=format:* %h %s') -AllowFail
    $body = @("PIKAN $Tag", '', '## 更新内容', '', ($subjects -join "`n")) -join "`n"
    Info "commit 范围：$range（$($subjects.Count) 条）"
}
else {
    $all = Get-Content -LiteralPath $Notes -Raw -Encoding UTF8
    $lines = $all -split "`r?`n"
    # 模板里第一个独占一行的 --- 之后才是正文，前面留给维护者写说明
    $sep = 0
    while ($sep -lt $lines.Count -and $lines[$sep].Trim() -ne '---') { $sep++ }
    $body = if ($sep -lt $lines.Count) { ($lines[($sep + 1)..($lines.Count - 1)] -join "`n").Trim() } else { $all.Trim() }
}

$certSha = if ($certItem) { Get-FileSha256 $certItem.FullName } else { '' }

$map = @{
    '{{TAG}}'         = $Tag
    '{{VERSION}}'     = ($Tag -replace '^v', '')
    '{{DATE}}'        = (Get-Date).ToString('yyyy-MM-dd')
    '{{COMMIT}}'      = $commitShort
    '{{SIZE_MB}}'     = [string]$sizeMb
    '{{SIZE_BYTES}}'  = $sizeBytes.ToString('N0')
    '{{SHA256}}'      = $sha
    '{{CERT_SHA256}}' = $certSha
}
foreach ($k in $map.Keys) { $body = $body.Replace($k, $map[$k]) }

# 剩下的 {{...}} 说明模板改了但这里没跟上，别悄悄发上去
$left = [regex]::Matches($body, '\{\{[A-Z_]+\}\}') | ForEach-Object { $_.Value } | Select-Object -Unique
if ($left) { Warn "正文里还有没被替换的占位符：$($left -join ', ')" }

$notesFile = Join-Path ([IO.Path]::GetTempPath()) "pikan-notes-$Tag-$([guid]::NewGuid().ToString('N').Substring(0, 8)).md"
$Utf8NoBom = New-Object System.Text.UTF8Encoding $false
[IO.File]::WriteAllText($notesFile, $body + "`n", $Utf8NoBom)
Ok "正文 $($body.Length) 字符 → $($([IO.Path]::GetFileName($notesFile)))"

# ---------------------------------------------------------------- [4/7] tag 推送

Step '4' "打 tag $Tag 并推送两个远端"

if ($Force) {
    foreach ($r in $tagExists.Keys) {
        if ($r -eq '__local' -or -not $tagExists[$r]) { continue }
        Info "删远端旧 tag：$r"
        Invoke-Git @('push', $r, '--delete', "refs/tags/$Tag") -AllowFail | Out-Null
    }
    if ($tagExists['__local']) { Invoke-Git @('tag', '-d', $Tag) -AllowFail | Out-Null }
}

if (-not (Invoke-Git @('tag', '-l', $Tag))) {
    Invoke-Git @('tag', '-a', $Tag, '-m', "PIKAN $Tag")
    Ok "已建附注 tag $Tag"
}
else { Info "本地 tag $Tag 已存在，复用" }

# 远端明明已经有同名 tag 却走到这里，说明删除失败或别人刚推同名 tag
if (-not $Force) {
    foreach ($r in @('origin', 'gitee')) {
        if ($r -eq 'origin' -and $SkipGitHub) { continue }
        if ($r -eq 'gitee' -and $SkipGitee) { continue }
        $has = Invoke-Git @('ls-remote', '--tags', $r, "refs/tags/$Tag") -AllowFail
        if ($has -match "refs/tags/$([regex]::Escape($Tag))$") { Die "$r 上出现了 tag $Tag（并发？）。要覆盖加 -Force -Yes。" }
    }
}

$pushTargets = @()
if (-not $SkipGitHub) { $pushTargets += 'origin' }
if (-not $SkipGitee) { $pushTargets += 'gitee' }

foreach ($r in $pushTargets) {
    # Gitee 建发行版要求远端先有这个 tag，所以顺序是：先推分支 → 再推 tag
    Invoke-Git @('fetch', $r, '--quiet') -AllowFail | Out-Null
    $behindAhead = (Invoke-Git @('rev-list', '--left-right', '--count', "refs/remotes/$r/$Branch...HEAD") -AllowFail | Select-Object -First 1)
    if ($LASTEXITCODE -eq 0 -and $behindAhead) {
        $parts = $behindAhead.Trim().Split([char[]]"`t ", [StringSplitOptions]::RemoveEmptyEntries)
        if ($parts.Count -eq 2) {
            $behind = [int]$parts[0]; $ahead = [int]$parts[1]
            if ($behind -gt 0) { Die "$r/$Branch 比本地新 $behind 个提交，先 pull 再发版。" }
            if ($ahead -gt 0) {
                Info "$r/$Branch 落后 $ahead 个提交，推送"
                Invoke-Git @('push', $r, "$Branch")
                Ok "已推 $Branch → $r"
            }
            else { Info "$r/$Branch 与本地一致" }
        }
    }
    else { Info "拿不到 $r/$Branch 的对比基准，跳过分支推送（tag 仍会推）" }

    Invoke-Git @('push', $r, $Tag)
    Ok "已推 tag $Tag → $r"
}

# ---------------------------------------------------------------- [5/7] GitHub

$ghUrl = $null
if ($SkipGitHub) {
    Step '5' 'GitHub Release（跳过）'
}
else {
    Step '5' "GitHub Release → $GitHubRepo"

    $exists = $false
    try {
        & gh release view $Tag -R $GitHubRepo 2>&1 | Out-Null
        $exists = ($LASTEXITCODE -eq 0)
    }
    catch { $exists = $false }

    $assetArgs = @()
    $assetArgs += "$Exe#PIKAN.exe"
    if ($certItem) { $assetArgs += "$($certItem.FullName)#$($certItem.Name)" }

    if ($exists) {
        Info "Release $Tag 已存在，更新正文"
        & gh release edit $Tag -R $GitHubRepo --title $Title --notes-file $notesFile
        if ($LASTEXITCODE -ne 0) { Die "gh release edit 失败（exit $LASTEXITCODE）" }

        $existing = & gh release view $Tag -R $GitHubRepo --json assets -q '.assets[].name'
        $existingNames = @()
        if ($LASTEXITCODE -eq 0 -and $existing) { $existingNames = $existing -split "`n" | ForEach-Object { $_.Trim() } }

        foreach ($pair in $assetArgs) {
            $name = ($pair -split '#', 2)[1]
            if ($existingNames -contains $name) {
                if (-not $Force) { Warn "附件 $name 已存在，跳过上传（要替换加 -Force -Yes）" ; continue }
                & gh release delete-asset $Tag $name -R $GitHubRepo -y
                if ($LASTEXITCODE -ne 0) { Die "gh release delete-asset $name 失败（exit $LASTEXITCODE）" }
                Info "已删旧附件 $name"
            }
            & gh release upload $Tag $pair -R $GitHubRepo --clobber
            if ($LASTEXITCODE -ne 0) { Die "gh release upload 失败（exit $LASTEXITCODE）" }
            Ok "上传 $name"
        }
    }
    else {
        $args = @('release', 'create', $Tag, '-R', $GitHubRepo, '--title', $Title, '--notes-file', $notesFile, '--latest')
        if ($Draft) { $args += '--draft' }
        $args += $assetArgs
        & gh @args
        if ($LASTEXITCODE -ne 0) { Die "gh release create 失败（exit $LASTEXITCODE）" }
        Ok "Release 已建，附件 $($assetArgs.Count) 个"
    }

    $ghUrl = "https://github.com/$GitHubRepo/releases/tag/$Tag"
    Info $ghUrl
}

# ---------------------------------------------------------------- [6/7] Gitee

$geeUrl = $null
if ($SkipGitee) {
    Step '6' 'Gitee 发行版（跳过）'
}
else {
    Step '6' "Gitee 发行版 → $GiteeRepo"

    $plain = Convert-SecureStringToPlain $GiteeToken
    if (-not $plain -and $env:GITEE_TOKEN) { $plain = $env:GITEE_TOKEN; Info '令牌来源：$env:GITEE_TOKEN' }
    if (-not $plain) {
        $sec = Read-Host 'Gitee 私人令牌（需要 projects 权限，输入不回显）' -AsSecureString
        $plain = Convert-SecureStringToPlain $sec
        Info '令牌来源：交互式输入'
    }
    if (-not $plain) { Die '没有 Gitee 令牌。只发 GitHub 就加 -SkipGitee。' }

    $api = "https://gitee.com/api/v5/repos/$GiteeRepo"

    $rel = $null
    try {
        $list = Invoke-RestMethod -Uri "$api/releases?access_token=$plain" -Method Get -TimeoutSec 60
        $rel = $list | Where-Object { $_.tag_name -eq $Tag } | Select-Object -First 1
    }
    catch { Die "列取 Gitee 发行版失败：$($_.Exception.Message)" }

    if (-not $rel) {
        Info '创建发行版'
        # Gitee 建发行版要求远端已有这个 tag——上一步已经推过去了
        $rel = Invoke-RestMethod -Uri "$api/releases" -Method Post -TimeoutSec 120 -Body @{
            access_token      = $plain
            tag_name          = $Tag
            target_commitish  = $Branch
            name              = $Title
            body              = $body
            prerelease        = 'false'
        }
        Ok "发行版已建，id=$($rel.id)"
    }
    else {
        # PATCH 必须 tag_name + name + body 三样同给，少一样就 400
        $rel = Invoke-RestMethod -Uri "$api/releases/$($rel.id)" -Method Patch -TimeoutSec 120 -Body @{
            access_token = $plain
            tag_name     = $Tag
            name         = $Title
            body         = $body
        }
        Info "发行版已存在（id=$($rel.id)），正文已更新"
    }

    $attachApi = "$api/releases/$($rel.id)/attach_files"

    # 附件名 → id，用于 -Force 时先删后传
    $existingAssets = @{}
    try {
        $assets = Invoke-RestMethod -Uri "$attachApi?access_token=$plain" -Method Get -TimeoutSec 60
        foreach ($a in $assets) { $existingAssets[$a.name] = $a.id }
    }
    catch { Warn "取附件列表失败：$($_.Exception.Message)" }

    $uploads = @(@{ Path = $Exe; Name = 'PIKAN.exe' })
    if ($certItem) { $uploads += @{ Path = $certItem.FullName; Name = $certItem.Name } }

    foreach ($u in $uploads) {
        if ($existingAssets.ContainsKey($u.Name)) {
            if (-not $Force) { Warn "附件 $($u.Name) 已存在，跳过上传（要替换加 -Force -Yes）"; continue }
            try {
                Invoke-WebRequest -Method Delete -TimeoutSec 60 -UseBasicParsing `
                    -Uri "$attachApi/$($existingAssets[$u.Name])" `
                    -ContentType 'application/x-www-form-urlencoded' `
                    -Body "access_token=$plain" | Out-Null
                Info "已删旧附件 $($u.Name)"
            }
            catch { Die "删除旧附件 $($u.Name) 失败：$($_.Exception.Message)" }
        }

        # -Form 里放 FileInfo，PowerShell 会按 multipart/form-data 发出去，文件名自动取磁盘名。
        # Gitee 这边 58 MB 要传 5 分钟左右，超时给到 30 分钟。
        $sizeHint = [math]::Round((Get-Item -LiteralPath $u.Path).Length / 1MB, 1)
        Write-Host "  上传 $($u.Name)（$sizeHint MB，Gitee 慢，耐心等）..." -ForegroundColor DarkGray
        try {
            Invoke-WebRequest -Uri "$attachApi" -Method Post -TimeoutSec 1800 -Form @{
                access_token = $plain
                file         = (Get-Item -LiteralPath $u.Path)
            } | Out-Null
        }
        catch { Die "上传 $($u.Name) 失败：$($_.Exception.Message)" }
        Ok "上传 $($u.Name)"
    }

    $geeUrl = "https://gitee.com/$GiteeRepo/releases/$Tag"
    Info $geeUrl
}

# ---------------------------------------------------------------- [7/7] 回下载校验

Step '7' '回下载校验 SHA256'

if ($SkipVerify) {
    Info '跳过（-SkipVerify）'
}
else {
    $targets = @()
    if ($ghUrl -and -not $Draft) { $targets += @{ Site = 'GitHub'; Url = "https://github.com/$GitHubRepo/releases/download/$Tag/PIKAN.exe" } }
    if ($geeUrl) { $targets += @{ Site = 'Gitee'; Url = "https://gitee.com/$GiteeRepo/releases/download/$Tag/PIKAN.exe" } }
    if ($ghUrl -and $Draft) { Warn '草稿 Release 的附件下载需要鉴权，跳过 GitHub 校验' }

    foreach ($t in $targets) {
        $tmp = Join-Path ([IO.Path]::GetTempPath()) "pikan-verify-$Tag-$($t.Site)-$([guid]::NewGuid().ToString('N').Substring(0, 8)).exe"
        try {
            Invoke-WebRequest -Uri $t.Url -OutFile $tmp -TimeoutSec 1800 -MaximumRedirection 5
            $down = Get-FileSha256 $tmp
            if ($down -eq $sha) { Ok "$($t.Site) 下载回来的哈希一致" }
            else { Warn "$($t.Site) 哈希不一致！`n      本地 $sha`n      远端 $down" }
        }
        catch { Warn "$($t.Site) 下载校验失败：$($_.Exception.Message)" }
        finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    }
}

if ($notesFile) { Remove-Item -LiteralPath $notesFile -Force -ErrorAction SilentlyContinue }

Write-Host "`n完成。$Tag 已发出：" -ForegroundColor Green
if ($ghUrl) { Write-Host "  GitHub  $ghUrl" -ForegroundColor White }
if ($geeUrl) { Write-Host "  Gitee   $geeUrl" -ForegroundColor White }
Write-Host "  SHA256  $sha" -ForegroundColor DarkGray
Write-Host "`n记得去 https://gitee.com/profile/personal_access_tokens 把这次用的令牌删掉。" -ForegroundColor Yellow
