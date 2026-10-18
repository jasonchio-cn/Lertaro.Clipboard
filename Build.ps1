[CmdletBinding()]
param([string]$Dotnet='', [string]$LertaroDir='')
$ErrorActionPreference='Stop'
if (!$Dotnet) {
    $localDotnet=Join-Path $PSScriptRoot '.local\dotnet\dotnet.exe'
    $Dotnet=if(Test-Path -LiteralPath $localDotnet){$localDotnet}else{'dotnet'}
}
$buildArgs=@('build',(Join-Path $PSScriptRoot 'Clipboard.csproj'),'-c','Release','--nologo')
if($LertaroDir){$buildArgs+=('-p:LertaroDir='+[IO.Path]::GetFullPath($LertaroDir))}
& $Dotnet @buildArgs
if($LASTEXITCODE -ne 0){ throw '构建失败。' }
$dll=Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\Lertaro.Plugins.Clipboard.dll'
((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash+'  Lertaro.Plugins.Clipboard.dll') | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'SHA256.txt') -Encoding ASCII
