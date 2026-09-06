[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$projectFile = Join-Path $projectRoot 'src\ResolumeConfigurator\ResolumeConfigurator.csproj'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot 'artifacts\release'
}
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}

[xml]$project = Get-Content -LiteralPath $projectFile -Raw
$versionNode = $project.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
    throw "The application version is missing from $projectFile"
}
$version = $versionNode.InnerText.Trim()

$temporaryRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar)
$buildDirectory = [IO.Path]::GetFullPath((Join-Path $temporaryRoot "ResolumeConfiguratorInstaller-$([Guid]::NewGuid().ToString('N'))"))
if (-not $buildDirectory.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installer build directory is outside the temporary workspace.'
}
$publishDirectory = Join-Path $buildDirectory 'payload'
$sedPath = Join-Path $buildDirectory 'package.sed'
$installerName = "Resolume-Arena-Configurator-v$version-$Runtime-Setup.exe"
$installerPath = Join-Path $OutputDirectory $installerName
$packagedInstallerPath = Join-Path $buildDirectory $installerName

New-Item -ItemType Directory -Force -Path $publishDirectory, $OutputDirectory | Out-Null

Write-Host "Publishing Resolume Arena Configurator $version for $Runtime..."
& dotnet publish $projectFile `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    --output $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-Local.ps1') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-Local.ps1') -Destination $publishDirectory

$payloadFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File | Sort-Object Name)
if ($payloadFiles.Count -eq 0) {
    throw "No files were published to $publishDirectory"
}

$sourceEntries = [Collections.Generic.List[string]]::new()
$stringEntries = [Collections.Generic.List[string]]::new()
for ($index = 0; $index -lt $payloadFiles.Count; $index++) {
    $token = "FILE$index"
    $sourceEntries.Add("%$token%=")
    $stringEntries.Add("$token=`"$($payloadFiles[$index].Name)`"")
}

$sedLines = @(
    '[Version]'
    'Class=IEXPRESS'
    'SEDVersion=3'
    ''
    '[Options]'
    'PackagePurpose=InstallApp'
    'ShowInstallProgramWindow=1'
    'HideExtractAnimation=0'
    'UseLongFileName=1'
    'InsideCompressed=0'
    'CAB_FixedSize=0'
    'CAB_ResvCodeSigning=0'
    'RebootMode=N'
    'InstallPrompt=%InstallPrompt%'
    'DisplayLicense=%DisplayLicense%'
    'FinishMessage=%FinishMessage%'
    'TargetName=%TargetName%'
    'FriendlyName=%FriendlyName%'
    'AppLaunched=%AppLaunched%'
    'PostInstallCmd=<None>'
    'AdminQuietInstCmd=%AdminQuietInstCmd%'
    'UserQuietInstCmd=%UserQuietInstCmd%'
    'SourceFiles=SourceFiles'
    ''
    '[SourceFiles]'
    "SourceFiles0=$publishDirectory\"
    ''
    '[SourceFiles0]'
) + $sourceEntries + @(
    ''
    '[Strings]'
    'InstallPrompt="Install Resolume Arena Configurator for the current user?"'
    'DisplayLicense=""'
    'FinishMessage="Resolume Arena Configurator was installed successfully."'
    "TargetName=$packagedInstallerPath"
    "FriendlyName=`"Resolume Arena Configurator $version Setup`""
    'AppLaunched="powershell.exe -NoProfile -ExecutionPolicy Bypass -File Install-Local.ps1"'
    'AdminQuietInstCmd="powershell.exe -NoProfile -ExecutionPolicy Bypass -File Install-Local.ps1"'
    'UserQuietInstCmd="powershell.exe -NoProfile -ExecutionPolicy Bypass -File Install-Local.ps1"'
) + $stringEntries

[IO.File]::WriteAllLines($sedPath, $sedLines, [Text.Encoding]::ASCII)
Write-Host "Packaging $installerName..."
$iexpressProcess = Start-Process `
    -FilePath "$env:WINDIR\System32\iexpress.exe" `
    -ArgumentList @('/N', '/Q', 'package.sed') `
    -WorkingDirectory $buildDirectory `
    -WindowStyle Hidden `
    -Wait `
    -PassThru
if ($iexpressProcess.ExitCode -ne 0) {
    throw "IExpress failed with exit code $($iexpressProcess.ExitCode)"
}

# IExpress can return before its cabinet worker has finished. Keeping this
# PowerShell process alive also prevents non-interactive build hosts from
# tearing down that child process with the parent job.
$packageDeadline = [DateTime]::UtcNow.AddMinutes(15)
$stableObservations = 0
$previousStamp = ''
while ($stableObservations -lt 5 -and [DateTime]::UtcNow -lt $packageDeadline) {
    Start-Sleep -Milliseconds 500
    if (-not (Test-Path -LiteralPath $packagedInstallerPath)) { continue }
    try {
        $package = Get-Item -LiteralPath $packagedInstallerPath
        $packageStream = [IO.File]::Open($packagedInstallerPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try {
            if ($packageStream.Length -eq 0) { continue }
            $stamp = "$($package.LastWriteTimeUtc.Ticks):$($packageStream.Length)"
        }
        finally { $packageStream.Dispose() }
        $stableObservations = if ($stamp -eq $previousStamp) { $stableObservations + 1 } else { 1 }
        $previousStamp = $stamp
    }
    catch [IO.IOException] { $stableObservations = 0 }
}
if ($stableObservations -lt 5) {
    throw "IExpress did not finish the expected installer within 15 minutes: $packagedInstallerPath"
}

Move-Item -LiteralPath $packagedInstallerPath -Destination $installerPath -Force

$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashPath = "$installerPath.sha256"
[IO.File]::WriteAllText($hashPath, "$hash  $installerName`r`n", [Text.Encoding]::ASCII)

$installer = Get-Item -LiteralPath $installerPath
$result = [PSCustomObject]@{
    Installer = $installer.FullName
    Version = $version
    SizeMB = [Math]::Round($installer.Length / 1MB, 2)
    SHA256 = $hash
}
$cleanupDirectory = (Resolve-Path -LiteralPath $buildDirectory).Path
if ($cleanupDirectory -ne $buildDirectory -or -not $cleanupDirectory.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installer cleanup directory changed or escaped the temporary workspace.'
}
Remove-Item -LiteralPath $cleanupDirectory -Recurse -Force
Write-Output $result
