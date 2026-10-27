# PIKAN 代码签名脚本（自签证书）
#
# 解决 SmartScreen 的 "Windows 已保护你的电脑 / 未识别的应用" 拦截：
# 未签名的 exe 没有发布者信誉，SmartScreen 一律拦；签上名之后至少会显示发布者，
# 把根证书导入目标机后即可正常启动。
#
# 用法（PowerShell 7）：
#   pwsh .\Sign.ps1                      # 签 .\PIKAN.exe
#   pwsh .\Sign.ps1 -Exe D:\PIKAN.exe    # 签指定文件
#   pwsh .\Sign.ps1 -Force               # 已有本证书签名也重新签
#   pwsh .\Sign.ps1 -NewCert             # 丢弃旧证书，重新签发一张
#
# 依赖：Windows SDK 的 signtool（脚本会自动在 Windows Kits 下找），签名时可联网打时间戳。

param(
    [string] $Exe = (Join-Path $PSScriptRoot 'PIKAN.exe'),
    [string] $Subject = 'CN=JacquelineWu (PIKAN), O=JacquelineWu, C=CN',
    [string] $TimestampServer = 'http://timestamp.digicert.com',
    [switch] $Force,
    [switch] $NewCert
)

$ErrorActionPreference = 'Stop'

# ---- 1. 找 signtool ----
$signtool = Get-ChildItem -Path 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1

if (-not $signtool) {
    throw '找不到 signtool.exe。请安装 Windows SDK（或 Visual Studio 的 "Windows 10/11 SDK" 组件）。'
}
Write-Host "signtool: $signtool"

if (-not (Test-Path $Exe)) {
    throw "目标文件不存在：$Exe"
}

# ---- 2. 取或建代码签名证书 ----
$cert = $null
if (-not $NewCert) {
    $cert = Get-ChildItem -Path Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $Subject } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

if (-not $cert) {
    Write-Host "生成代码签名证书：$Subject"
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(5) `
        -FriendlyName 'PIKAN 代码签名（自签）'
}

Write-Host "证书: $($cert.Thumbprint)  有效期至 $($cert.NotAfter)"

# ---- 3. 让本机信任它：受信任的根 + 受信任的发布者 ----
foreach ($storeName in @('Root', 'TrustedPublisher')) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, 'CurrentUser')
    $store.Open('ReadWrite')
    $store.Add($cert)
    $store.Close()
    Write-Host "已导入 CurrentUser\$storeName"
}

# ---- 4. 导出公钥证书，供其它机器导入 ----
$cerPath = Join-Path $PSScriptRoot 'PIKAN-CodeSigning.cer'
Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT -Force | Out-Null
Write-Host "已导出公钥证书：$cerPath"

# ---- 5. 签名（已签过就跳过，避免叠加多个签名）----
$existing = Get-AuthenticodeSignature -FilePath $Exe
if ($existing.Status -eq 'Valid' -and $existing.SignerCertificate.Thumbprint -eq $cert.Thumbprint -and -not $Force) {
    Write-Host "已是本证书的有效签名，跳过（要重签加 -Force）"
}
else {
    Write-Host "签名中：$Exe"
    & $signtool sign /fd SHA256 /td SHA256 /sha1 $cert.Thumbprint /tr $TimestampServer /d 'PIKAN Image Viewer' $Exe
    if ($LASTEXITCODE -ne 0) { throw "签名失败（exit=$LASTEXITCODE）" }
}

# ---- 6. 验证 ----
& $signtool verify /pa $Exe
if ($LASTEXITCODE -ne 0) { throw '签名验证失败' }

$sig = Get-AuthenticodeSignature -FilePath $Exe
Write-Host ''
Write-Host "签名状态 : $($sig.Status)"
Write-Host "发布者   : $($sig.SignerCertificate.Subject)"
Write-Host "时间戳   : $($sig.TimeStamperCertificate.Subject)"
Write-Host ''
Write-Host '其它机器导入信任（管理员或当前用户均可，拷过去后执行）：'
Write-Host "  certutil -addstore -user Root PIKAN-CodeSigning.cer"
Write-Host "  certutil -addstore -user TrustedPublisher PIKAN-CodeSigning.cer"
