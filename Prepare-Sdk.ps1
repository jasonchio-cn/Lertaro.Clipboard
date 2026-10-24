[CmdletBinding()]
param([string]$Destination = (Join-Path $PSScriptRoot '.local\sdk'))
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$archive=Join-Path ([IO.Path]::GetTempPath()) ('lertaro-sdk-'+[guid]::NewGuid().ToString('N')+'.zip')
$uri='https://github.com/Lertaro/Lertaro/releases/download/v5.10.0/Lertaro-Portable.zip'
$expected='1C57722079504BBF4E9F51DD1DBA9211306712DD0EDB0AFB0BD6947F8A890EB7'
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
        if($version.ToString(3) -ne '2.3.0'){throw '下载 SDK 的版本不是 2.3.0。'}
        Write-Host ('已准备 Lertaro Plugin SDK '+$version+'：'+[IO.Path]::GetFullPath($target))
    } finally {$zip.Dispose()}
} finally {if(Test-Path -LiteralPath $archive){Remove-Item -LiteralPath $archive}}
