[CmdletBinding()]
param([string]$Destination = (Join-Path $PSScriptRoot '.local\sdk'))
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$archive=Join-Path ([IO.Path]::GetTempPath()) ('lertaro-sdk-'+[guid]::NewGuid().ToString('N')+'.zip')
$uri='https://github.com/Lertaro/Lertaro/releases/download/v5.9.6/Lertaro-Portable.zip'
$expected='AF4B4F965CE9FBACC57CFA4E8E525F8C65A3397F8A8F98642AE66BCA5F78FA84'
try {
    [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $uri -OutFile $archive -UseBasicParsing
    if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $expected){throw '官方 SDK 下载校验失败。'}
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entries=@($zip.Entries | Where-Object { $_.Name -eq 'Lertaro.PluginSdk.dll' })
        if($entries.Count -ne 1){throw '下载包中的 SDK 数量不符合预期。'}
        $target=Join-Path $Destination 'Lertaro.PluginSdk.dll'
        $source=$entries[0].Open()
        try {$output=[IO.File]::Create($target);try{$source.CopyTo($output)}finally{$output.Dispose()}}finally{$source.Dispose()}
        $version=[Reflection.AssemblyName]::GetAssemblyName($target).Version
        if($version.ToString(3) -ne '2.1.3'){throw '下载 SDK 的版本不是 2.1.3。'}
        Write-Host ('已准备 Lertaro Plugin SDK '+$version+'：'+[IO.Path]::GetFullPath($target))
    } finally {$zip.Dispose()}
} finally {if(Test-Path -LiteralPath $archive){Remove-Item -LiteralPath $archive}}
