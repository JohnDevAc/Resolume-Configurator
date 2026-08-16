[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\Resolume Arena Configurator'
$process = Get-Process -Name 'Resolume Arena Configurator' -ErrorAction SilentlyContinue
if ($process) {
    $process.CloseMainWindow() | Out-Null
    $process.WaitForExit(5000) | Out-Null
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
if (Test-Path -LiteralPath $installDirectory) { Remove-Item -LiteralPath $installDirectory -Recurse -Force }
