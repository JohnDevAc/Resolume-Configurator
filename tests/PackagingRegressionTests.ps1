[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $repo 'scripts\Payload.ps1')
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "resolume-script-tests-$([Guid]::NewGuid().ToString('N'))"))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
try {
    foreach ($name in @('Resolume Arena Configurator.exe', 'LICENSE', 'Install-Local.ps1', 'Uninstall-Local.ps1', 'Payload.ps1', 'native.dll')) {
        [IO.File]::WriteAllText((Join-Path $fixtureRoot $name), "fixture $name")
    }
    New-PayloadManifest -Directory $fixtureRoot
    $manifest = Test-PayloadManifest -Directory $fixtureRoot
    if (@($manifest.Files).Count -ne 6) { throw 'Manifest omitted a published dependency.' }
    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'native.dll'), 'changed')
    $rejected = $false
    try { Test-PayloadManifest -Directory $fixtureRoot | Out-Null } catch { $rejected = $_.Exception.Message -like '*missing or changed*' }
    if (-not $rejected) { throw 'Changed dependency was accepted.' }
    New-PayloadManifest -Directory $fixtureRoot
    [IO.File]::Delete((Join-Path $fixtureRoot 'Uninstall-Local.ps1'))
    $rejected = $false
    try { Test-PayloadManifest -Directory $fixtureRoot | Out-Null } catch { $rejected = $_.Exception.Message -like '*missing or changed*' }
    if (-not $rejected) { throw 'Missing uninstaller was accepted.' }
    Write-Output 'PASS payload integrity and required files'

    $nested = Join-Path $fixtureRoot 'nested'
    New-Item -ItemType Directory -Path $nested | Out-Null
    $rejected = $false
    try { New-PayloadManifest -Directory $fixtureRoot } catch { $rejected = $_.Exception.Message -like '*flat publish payload*' }
    if (-not $rejected) { throw 'Nested payload content was silently omitted.' }
    Write-Output 'PASS nested payload rejection'

    $unicode = Join-Path $fixtureRoot ('Jos' + [char]0x00e9)
    $rejected = $false
    try { New-InstallerWorkspace -StagingDirectory $unicode | Out-Null } catch { $rejected = $_.Exception.Message -like '*ASCII staging path*' }
    if (-not $rejected) { throw 'An unsafe SED staging path was accepted.' }
    Write-Output 'PASS explicit Unicode staging validation'

    # All destructive commands below are replaced in a child scope. No real
    # processes, shortcuts, installation files or registry keys are touched.
    & {
        $removals = [Collections.Generic.List[string]]::new()
        function Get-Process {
            $process = [pscustomobject]@{ HasExited = $false }
            $process | Add-Member ScriptMethod CloseMainWindow { return $false }
            $process | Add-Member ScriptMethod WaitForExit { param($milliseconds) return $false }
            return $process
        }
        function Remove-Item { param($LiteralPath, [switch]$Force, [switch]$Recurse) $removals.Add($LiteralPath) }
        $rejected = $false
        try { & (Join-Path $repo 'scripts\Uninstall-Local.ps1') } catch { $rejected = $_.Exception.Message -like '*helper to finish*' }
        if (-not $rejected -or $removals.Count -ne 0) { throw 'Uninstall changed files/registration while a helper was running.' }
    }
    Write-Output 'PASS uninstall retains registration for a running helper'

    & {
        $removals = [Collections.Generic.List[string]]::new()
        function Get-Process { return @() }
        function Test-Path { param($LiteralPath) return $true }
        function Resolve-Path { param($LiteralPath) return [pscustomobject]@{ Path = $LiteralPath } }
        function Get-Item { param($LiteralPath) return [pscustomobject]@{ Attributes = [IO.FileAttributes]::Directory } }
        function Remove-Item {
            param($LiteralPath, [switch]$Force, [switch]$Recurse)
            $removals.Add($LiteralPath)
            throw [IO.IOException]::new('Locked fixture executable')
        }
        $rejected = $false
        $failureMessage = ''
        try { & (Join-Path $repo 'scripts\Uninstall-Local.ps1') } catch { $failureMessage = $_.Exception.Message; $rejected = $failureMessage -like '*Locked fixture*' }
        if (-not $rejected -or $removals.Count -ne 1 -or $removals[0] -notlike '*Programs\Resolume Arena Configurator') {
            throw "Uninstall ordering check failed: $failureMessage. Removals: $($removals -join ', ')"
        }
    }
    Write-Output 'PASS uninstall file failure preserves shortcuts and registration'
}
finally {
    $resolved = (Resolve-Path -LiteralPath $fixtureRoot).Path
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($resolved -ne $fixtureRoot -or -not $resolved.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unsafe test cleanup path.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
