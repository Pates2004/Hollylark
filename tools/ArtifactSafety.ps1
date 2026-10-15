. (Join-Path $PSScriptRoot '..\project_materials.ps1')

function Move-VerifiedProjectArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Purpose
    )
    $sourcePath = [IO.Path]::GetFullPath((Join-ProjectMaterialPath $Root $Source))
    $destinationPath = [IO.Path]::GetFullPath((Join-ProjectMaterialPath $Root $Destination))
    if ($destinationPath.StartsWith($sourcePath + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Destination cannot be inside the source artifact.'
    }
    foreach ($path in @($sourcePath, $destinationPath)) {
        if (-not (Test-ProjectMaterialPath $path $Root)) {
            throw "Artifact path is outside the project: $path"
        }
        $ancestor = $path
        while ($ancestor) {
            if (Test-Path -LiteralPath $ancestor) {
                if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw "Reparse points are not allowed: $ancestor"
                }
            }
            $ancestor = Split-Path -Parent $ancestor
        }
    }
    if (Test-Path -LiteralPath $destinationPath) { throw "Archive already exists: $destinationPath" }
    $sourceItem = Get-Item -LiteralPath $sourcePath -Force
    $files = @($sourceItem)
    if ($sourceItem.PSIsContainer) {
        $children = @(Get-ChildItem -LiteralPath $sourcePath -Recurse -Force)
        if ($children | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
            throw 'Artifact contains a reparse point.'
        }
        $files = @($children | Where-Object { -not $_.PSIsContainer })
    }
    $running = Get-Process | Where-Object {
        try { $_.Path -and ($_.Path -eq $sourcePath -or $_.Path.StartsWith($sourcePath + '\', [StringComparison]::OrdinalIgnoreCase)) }
        catch { $false }
    }
    if ($running) { throw "Artifact is in use by a running process: $sourcePath" }
    $hashes = @{}
    foreach ($file in $files) {
        $handle = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        $handle.Dispose()
        $relative = if ($sourceItem.PSIsContainer) { $file.FullName.Substring($sourcePath.Length + 1) } else { '' }
        $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    Write-Host "$Purpose`: $Source -> $Destination ($($files.Count) files; checked locks and SHA256)"
    New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
    Move-Item -LiteralPath $sourcePath -Destination $destinationPath
    foreach ($relative in $hashes.Keys) {
        $target = if ($relative) { Join-Path $destinationPath $relative } else { $destinationPath }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hashes[$relative]) {
            throw "Archive integrity check failed: $target"
        }
    }
    if ($sourceItem.PSIsContainer -and @(Get-ChildItem -LiteralPath $destinationPath -File -Recurse -Force).Count -ne $files.Count) {
        throw "Archive file count mismatch: $destinationPath"
    }
}
