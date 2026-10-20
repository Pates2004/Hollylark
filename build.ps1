. (Join-Path $PSScriptRoot 'project_materials.ps1')

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
. (Join-ProjectMaterialPath $projectRoot 'tools\ArtifactSafety.ps1')
$buildStamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$previous = "Trash\before-Hollylark-$buildStamp"
$staging = Join-ProjectMaterialPath $projectRoot "Workspace\Publish\Hollylark-$buildStamp"
Push-Location -LiteralPath $projectRoot
try {
    $validationReports = Join-ProjectMaterialPath $projectRoot 'Workspace\Build\bin\Hollylark.Validation\Release\net10.0-windows\reports'
    if (Test-Path -LiteralPath $validationReports) {
        Move-VerifiedProjectArtifact -Root $projectRoot -Source $validationReports -Destination "Workspace\Validation\before-publish-$buildStamp\reports" -Purpose 'Preserve validation reports separately from archived binaries'
    }
    if (Test-Path -LiteralPath (Join-ProjectMaterialPath $projectRoot 'Workspace\Build\bin')) {
        Move-VerifiedProjectArtifact -Root $projectRoot -Source 'Workspace\Build\bin' -Destination "$previous\Workspace\Build\bin" -Purpose 'Superseded build outputs; validation evidence preserved separately'
    }
    dotnet publish src/LarkControl.Windows/LarkControl.Windows.csproj -c Release -r win-x64 --self-contained false -p:PublishSelfContained=false -p:PublishSingleFile=true -p:UseAppHost=true -p:PublishTrimmed=false -o $staging --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publishing failed; the installed release has not been replaced.' }
    if (-not (Test-Path -LiteralPath "$staging\Hollylark.exe")) { throw 'Published executable is missing.' }
    foreach ($language in @('pl', 'en')) {
        $catalog = "$staging\langs\$language.lng"
        if (-not (Test-Path -LiteralPath $catalog)) { throw "Published language file is missing: $language" }
        if ((Get-FileHash -LiteralPath $catalog).Hash -ne (Get-FileHash -LiteralPath "src\LarkControl.Windows\langs\$language.lng").Hash) {
            throw "Published language file differs from the validated source: $language"
        }
    }
    $installation = Join-ProjectMaterialPath $projectRoot 'installfiles'
    if (Test-Path -LiteralPath $installation) {
        $running = Get-Process | Where-Object {
            try { $_.Path -and $_.Path.StartsWith($installation + '\', [StringComparison]::OrdinalIgnoreCase) }
            catch { $false }
        }
        if ($running) { throw 'Close Hollylark before replacing the installed release. No user process is stopped.' }
        $releaseItems = @(Get-ChildItem -LiteralPath $installation -Force | Where-Object { $_.Name -notin @('data', 'reports') })
        foreach ($item in $releaseItems) {
            if ($item.Name -notin @('Hollylark.exe', 'langs', 'SHA256SUMS.txt', 'README.md', 'README.pl.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt') -and
                $item.Name -notmatch '^Hollylark-\d+\.\d+\.\d+-win-x64\.zip$') {
                throw "Unknown installation item must be reviewed before publication: $($item.FullName)"
            }
        }
        foreach ($item in $releaseItems) {
            Move-VerifiedProjectArtifact -Root $projectRoot -Source $item.FullName -Destination "$previous\installfiles\$($item.Name)" -Purpose 'Superseded release file; application data stays in place'
        }
        foreach ($item in @(Get-ChildItem -LiteralPath $staging -Force)) {
            Move-VerifiedProjectArtifact -Root $projectRoot -Source $item.FullName -Destination "installfiles\$($item.Name)" -Purpose 'Current release file'
        }
    }
    else {
        Move-VerifiedProjectArtifact -Root $projectRoot -Source $staging -Destination 'installfiles' -Purpose 'Current release'
    }
    Get-FileHash -LiteralPath (Join-ProjectMaterialPath $projectRoot 'installfiles\Hollylark.exe') -Algorithm SHA256
}
finally {
    Pop-Location
}
