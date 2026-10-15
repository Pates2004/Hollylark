function Get-ProjectMaterialRoot {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][ValidateSet('Forme', 'Workspace', 'Snapshots', 'Trash')][string]$Category
    )
    $source = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\', '/')
    $drive = if ([IO.Directory]::GetLogicalDrives() -contains 'D:\') { 'D:\' } else { 'C:\' }
    return [IO.Path]::Combine($drive, 'work', $Category, [IO.Path]::GetFileName($source))
}

function Join-ProjectMaterialPath {
    param(
        [Parameter(Mandatory = $true, Position = 0)][string]$Path,
        [Parameter(Mandatory = $true, Position = 1)][string]$ChildPath
    )
    if ([IO.Path]::IsPathRooted($ChildPath)) { return [IO.Path]::GetFullPath($ChildPath) }
    $root = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $parts = $ChildPath -split '[\\/]', 2
    $category = switch ($parts[0].ToLowerInvariant()) {
        'forme' { 'Forme' }
        'workspace' { 'Workspace' }
        'snapshots' { 'Snapshots' }
        'trash' { 'Trash' }
    }
    if ($category) {
        $root = Get-ProjectMaterialRoot -ProjectRoot $root -Category $category
        $relative = if ($parts.Count -eq 2) { $parts[1] } else { '' }
    }
    else { $relative = $ChildPath }
    $result = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $relative))
    if (-not $result.Equals($root, [StringComparison]::OrdinalIgnoreCase) -and
        -not $result.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes the permitted project area: $ChildPath"
    }
    return $result
}

function Test-ProjectMaterialPath {
    param([string]$Path, [string]$ProjectRoot)
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $roots = @([IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\', '/'))
    foreach ($category in @('Forme', 'Workspace', 'Snapshots', 'Trash')) {
        $roots += Get-ProjectMaterialRoot -ProjectRoot $ProjectRoot -Category $category
    }
    foreach ($root in $roots) {
        if ($full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

$env:ProjectMaterialsDrive = if ([IO.Directory]::GetLogicalDrives() -contains 'D:\') { 'D:' } else { 'C:' }
