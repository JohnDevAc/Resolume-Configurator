[CmdletBinding()]
param([ValidateSet('win-x64')][string]$Runtime = 'win-x64', [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Payload.ps1')
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $projectRoot 'artifacts\publish\win-x64' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
& dotnet publish (Join-Path $projectRoot 'src\ResolumeConfigurator\ResolumeConfigurator.csproj') --configuration Release --runtime $Runtime --self-contained true -p:PublishSingleFile=true --output $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
foreach ($script in @('Install-Local.ps1', 'Uninstall-Local.ps1', 'Payload.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $OutputDirectory -Force
}
New-PayloadManifest -Directory $OutputDirectory
