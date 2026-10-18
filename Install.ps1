[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$LertaroDir,
    [string]$ReceiptPath = '',
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$receipt = [ordered]@{success=$false; action='install'; version=''; destination=''; error=''}
try {
    $root = [IO.Path]::GetFullPath($LertaroDir).TrimEnd('\')
    if (!(Test-Path -LiteralPath (Join-Path $root 'Lertaro.App.exe'))) { throw '目标目录不是 Lertaro 安装目录。' }
    $destination = Join-Path $root 'Plugins\Lertaro.Plugins.Clipboard'
    $receipt.destination = $destination
    $dll = Join-Path $destination 'Lertaro.Plugins.Clipboard.dll'
    if ($Uninstall) {
        $receipt.action = 'uninstall'
        if (Test-Path -LiteralPath $dll) {
            # Preserve both the old DLL and all history. The host does not scan .disabled files.
            Move-Item -LiteralPath $dll -Destination ($dll+'.'+(Get-Date -Format 'yyyyMMddHHmmss')+'.disabled')
        }
        Write-Host '插件已停用；重新启动 Lertaro 生效。历史数据已保留。'
    } else {
        $source = Join-Path $PSScriptRoot 'Lertaro.Plugins.Clipboard.dll'
        if (!(Test-Path -LiteralPath $source)) { $source = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\Lertaro.Plugins.Clipboard.dll' }
        $hashFile = Join-Path $PSScriptRoot 'SHA256.txt'
        if (!(Test-Path -LiteralPath $source) -or !(Test-Path -LiteralPath $hashFile)) { throw '安装包不完整，请先构建或重新解压发行包。' }
        $receipt.version = [Reflection.AssemblyName]::GetAssemblyName($source).Version.ToString(3)
        $expected = (Get-Content -LiteralPath $hashFile -Raw).Trim().Split(' ')[0]
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $expected) { throw 'DLL 校验失败，未进行安装。' }
        $sdk = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $root 'Lertaro.PluginSdk.dll')).Version
        if ($sdk.Major -ne 2 -or $sdk.Minor -ne 1 -or $sdk.Build -ne 3) { throw "此包验证的 SDK 为 2.1.3，目标为 $sdk，请先验证兼容性。" }
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $temp = Join-Path $destination ('clipboard-'+[guid]::NewGuid().ToString('N')+'.tmp')
        try {
            Copy-Item -LiteralPath $source -Destination $temp
            if ((Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash -ine $expected) { throw '复制后校验失败。' }
            if (Test-Path -LiteralPath $dll) {
                # Atomic replacement keeps the previous plugin available for rollback.
                [IO.File]::Replace($temp,$dll,($dll+'.'+(Get-Date -Format 'yyyyMMddHHmmss')+'.bak'))
            } else { Move-Item -LiteralPath $temp -Destination $dll }
        } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp } }
        Write-Host '剪贴板插件已安装。请完全退出并重新打开 Lertaro，保持普通用户权限运行。'
    }
    $receipt.success = $true
} catch {
    $receipt.error = $_.Exception.Message
    Write-Warning ('安装未完成：'+$receipt.error+' 若是访问被拒绝，请以管理员身份执行此安装脚本；升级时请先退出 Lertaro。')
} finally {
    if ($ReceiptPath) { $receipt | ConvertTo-Json | Set-Content -LiteralPath $ReceiptPath -Encoding UTF8 }
}
if (!$receipt.success) { exit 1 }
