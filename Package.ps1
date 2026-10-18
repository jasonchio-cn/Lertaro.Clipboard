[CmdletBinding()]
param([string]$Dotnet='', [string]$LertaroDir='', [switch]$SkipTests)
$ErrorActionPreference='Stop'
if(!$Dotnet){$local=Join-Path $PSScriptRoot '.local\dotnet\dotnet.exe';$Dotnet=if(Test-Path -LiteralPath $local){$local}else{'dotnet'}}
& (Join-Path $PSScriptRoot 'Build.ps1') -Dotnet $Dotnet -LertaroDir $LertaroDir
if(!$SkipTests){
    $testArgs=@('test',(Join-Path $PSScriptRoot 'Tests\Clipboard.Tests.csproj'),'-c','Release','--nologo','--results-directory',(Join-Path $PSScriptRoot 'artifacts\test-results'))
    if($LertaroDir){$testArgs+=('-p:LertaroDir='+[IO.Path]::GetFullPath($LertaroDir))}
    & $Dotnet @testArgs
    if($LASTEXITCODE -ne 0){throw '测试失败，未生成发行包。'}
}
$dll=Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\Lertaro.Plugins.Clipboard.dll'
$version=[Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString(3)
$artifacts=Join-Path $PSScriptRoot 'artifacts'
$stage=Join-Path $artifacts ('stage-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath $dll -Destination $stage
foreach($name in @('Install.ps1','README.md','LICENSE','CHANGELOG.md','SHA256.txt')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $stage}
$screenshots=Join-Path $PSScriptRoot 'docs\screenshots'
if(Test-Path -LiteralPath $screenshots){New-Item -ItemType Directory -Path (Join-Path $stage 'docs') -Force | Out-Null;Copy-Item -LiteralPath $screenshots -Destination (Join-Path $stage 'docs\screenshots') -Recurse}
$package=Join-Path $artifacts ('Lertaro-Clipboard-'+$version+'.zip')
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $package -Force
((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash+'  '+[IO.Path]::GetFileName($package)) | Set-Content -LiteralPath ($package+'.sha256') -Encoding ASCII
Write-Host ('安装包：'+$package)
