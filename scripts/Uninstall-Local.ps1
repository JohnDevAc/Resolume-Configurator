[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$programsDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs')).TrimEnd([IO.Path]::DirectorySeparatorChar)
$installDirectory = [IO.Path]::GetFullPath((Join-Path $programsDirectory 'Resolume Arena Configurator'))
if (-not $installDirectory.StartsWith($programsDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Uninstall directory is outside the expected Programs directory.'
}
foreach ($process in @(Get-Process -Name 'Resolume Arena Configurator' -ErrorAction SilentlyContinue)) {
    $process.CloseMainWindow() | Out-Null
    $process.WaitForExit(5000) | Out-Null
    if (-not $process.HasExited) { throw 'Close Resolume Arena Configurator and wait for its helper to finish before uninstalling. The installation and registration have been retained.' }
}

# Preserve Installed Apps registration and shortcuts if removal fails.
if (Test-Path -LiteralPath $installDirectory) {
    $resolved = (Resolve-Path -LiteralPath $installDirectory).Path
    if ($resolved -ne $installDirectory -or ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing to uninstall from a redirected installation directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

$shortcuts = @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Resolume Arena Configurator.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Resolume Arena Configurator.lnk')
)
foreach ($shortcut in $shortcuts) {
    if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
}

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ResolumeArenaConfigurator'
if (Test-Path -LiteralPath $uninstallKey) { Remove-Item -LiteralPath $uninstallKey -Recurse -Force }
