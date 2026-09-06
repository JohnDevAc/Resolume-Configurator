[CmdletBinding()]
param(
    [string]$SourceDirectory,
    [switch]$NoDesktopShortcut,
    [string]$Publisher = 'John Lightfoot'
)

$ErrorActionPreference = 'Stop'
$executableName = 'Resolume Arena Configurator.exe'
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    $adjacentExecutable = Join-Path $PSScriptRoot $executableName
    $SourceDirectory = if (Test-Path -LiteralPath $adjacentExecutable) {
        $PSScriptRoot
    }
    else {
        Join-Path $PSScriptRoot '..\artifacts\publish\win-x64'
    }
}
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$sourceExecutable = Join-Path $source $executableName
if (-not (Test-Path -LiteralPath $sourceExecutable)) {
    throw "Published app not found: $sourceExecutable"
}
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($sourceExecutable)
$displayVersion = if ([string]::IsNullOrWhiteSpace($versionInfo.ProductVersion)) {
    $versionInfo.FileVersion
}
else {
    $versionInfo.ProductVersion.Split('+')[0]
}
if ([string]::IsNullOrWhiteSpace($displayVersion)) {
    throw "Could not determine the published app version: $sourceExecutable"
}

$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\Resolume Arena Configurator'
$runningApps = @(Get-Process -Name 'Resolume Arena Configurator' -ErrorAction SilentlyContinue)
foreach ($runningApp in $runningApps) {
    $runningApp.CloseMainWindow() | Out-Null
    $runningApp.WaitForExit(5000) | Out-Null
    if (-not $runningApp.HasExited) {
        throw 'Close Resolume Arena Configurator before installing this update.'
    }
}
New-Item -ItemType Directory -Force -Path $installDirectory | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $installDirectory -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-Local.ps1') -Destination $installDirectory -Force

$installedExecutable = Join-Path $installDirectory $executableName
$shell = New-Object -ComObject WScript.Shell
$startMenuDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
New-Item -ItemType Directory -Force -Path $startMenuDirectory | Out-Null
$startMenuShortcut = Join-Path $startMenuDirectory 'Resolume Arena Configurator.lnk'
$shortcut = $shell.CreateShortcut($startMenuShortcut)
$shortcut.TargetPath = $installedExecutable
$shortcut.WorkingDirectory = $installDirectory
$shortcut.Description = 'Configure Resolume Arena from NDI Job Configurator'
$shortcut.IconLocation = "$installedExecutable,0"
$shortcut.Save()

if (-not $NoDesktopShortcut) {
    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Resolume Arena Configurator.lnk'
    $shortcut = $shell.CreateShortcut($desktopShortcut)
    $shortcut.TargetPath = $installedExecutable
    $shortcut.WorkingDirectory = $installDirectory
    $shortcut.Description = 'Configure Resolume Arena from NDI Job Configurator'
    $shortcut.IconLocation = "$installedExecutable,0"
    $shortcut.Save()
}

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ResolumeArenaConfigurator'
New-Item -Path $uninstallKey -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'Resolume Arena Configurator' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value $displayVersion -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name Publisher -Value $Publisher -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installDirectory -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value "$installedExecutable,0" -PropertyType String -Force | Out-Null
$estimatedSize = [Math]::Ceiling((Get-ChildItem -LiteralPath $installDirectory -File -Recurse | Measure-Object -Property Length -Sum).Sum / 1KB)
New-ItemProperty -Path $uninstallKey -Name EstimatedSize -Value $estimatedSize -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name InstallDate -Value (Get-Date -Format 'yyyyMMdd') -PropertyType String -Force | Out-Null
$uninstallScript = Join-Path $installDirectory 'Uninstall-Local.ps1'
$uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`""
New-ItemProperty -Path $uninstallKey -Name UninstallString -Value $uninstallCommand -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name QuietUninstallString -Value $uninstallCommand -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null

Write-Output $installedExecutable
