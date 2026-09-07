function New-PayloadManifest {
    param([Parameter(Mandatory)][string]$Directory)
    if (@(Get-ChildItem -LiteralPath $Directory -Directory).Count -gt 0) {
        throw 'The installer supports a flat publish payload. Nested content must be packaged explicitly before release.'
    }
    foreach ($required in @('Resolume Arena Configurator.exe', 'LICENSE', 'Install-Local.ps1', 'Uninstall-Local.ps1', 'Payload.ps1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $required) -PathType Leaf)) { throw "Required payload file missing: $required" }
    }
    $entries = @(Get-ChildItem -LiteralPath $Directory -File | Where-Object Name -ne 'payload-manifest.json' | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{ Name = $_.Name; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    $json = @{ SchemaVersion = 1; Files = $entries } | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText((Join-Path $Directory 'payload-manifest.json'), $json, [Text.UTF8Encoding]::new($false))
}

function Test-PayloadManifest {
    param([Parameter(Mandatory)][string]$Directory)
    $manifestPath = Join-Path $Directory 'payload-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'The publish payload has no manifest. Build it with scripts\Publish-Local.ps1 or Build-Installer.ps1 before installing.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.SchemaVersion -ne 1 -or @($manifest.Files).Count -eq 0) { throw 'Unsupported or empty payload manifest.' }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.Files) {
        $name = [string]$entry.Name
        if ([string]::IsNullOrWhiteSpace($name) -or [IO.Path]::GetFileName($name) -ne $name -or $name -in @('.', '..') -or $name.Contains(':') -or -not $names.Add($name)) {
            throw 'Invalid or duplicate payload manifest filename.'
        }
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.SHA256) {
            throw "Payload file missing or changed: $name. Rebuild or extract the complete package before installing."
        }
    }
    foreach ($required in @('Resolume Arena Configurator.exe', 'LICENSE', 'Install-Local.ps1', 'Uninstall-Local.ps1', 'Payload.ps1')) {
        if (-not $names.Contains($required)) { throw "Required payload entry missing: $required" }
    }
    return $manifest
}

function New-InstallerWorkspace {
    param([string]$StagingDirectory)
    $candidates = if ([string]::IsNullOrWhiteSpace($StagingDirectory)) {
        @([IO.Path]::GetTempPath(), [Environment]::GetFolderPath('CommonDocuments'))
    } else { @($StagingDirectory) }
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $root = [IO.Path]::GetFullPath($candidate).TrimEnd([IO.Path]::DirectorySeparatorChar)
        # IExpress SED is ASCII. Never replace characters in a filesystem path.
        if ($root -match '[^\x20-\x7E]|%') { continue }
        $directory = [IO.Path]::GetFullPath((Join-Path $root "ResolumeConfiguratorInstaller-$([Guid]::NewGuid().ToString('N'))"))
        if (-not $directory.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe installer staging path.' }
        try {
            New-Item -ItemType Directory -Path $directory -ErrorAction Stop | Out-Null
            return [pscustomobject]@{ Root = $root; Directory = $directory }
        } catch [System.UnauthorizedAccessException] { continue }
    }
    throw 'IExpress needs a writable ASCII staging path. Specify -StagingDirectory with such a directory; the final output directory can contain Unicode.'
}
