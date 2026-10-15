. (Join-Path $PSScriptRoot 'project_materials.ps1')

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
. (Join-ProjectMaterialPath $projectRoot 'tools\ArtifactSafety.ps1')
$buildStamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$snapshot = "snapshots\before-Hollylark-$buildStamp"
$staging = Join-ProjectMaterialPath $projectRoot "Workspace\Publish\Hollylark-$buildStamp"
Push-Location -LiteralPath $projectRoot
try {
    $validationReports = Join-ProjectMaterialPath $projectRoot 'Workspace\Build\bin\Hollylark.Validation\Release\net10.0-windows\reports'
    if (Test-Path -LiteralPath $validationReports) {
        Move-VerifiedProjectArtifact -Root $projectRoot -Source $validationReports -Destination "Workspace\Validation\before-publish-$buildStamp\reports" -Purpose 'Preserve validation reports separately from archived binaries'
    }
    if (Test-Path -LiteralPath (Join-ProjectMaterialPath $projectRoot 'Workspace\Build\bin')) {
        Move-VerifiedProjectArtifact -Root $projectRoot -Source 'Workspace\Build\bin' -Destination "$snapshot\Workspace\Build\bin" -Purpose 'Previous build outputs'
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
    if (Test-Path -LiteralPath (Join-ProjectMaterialPath $projectRoot 'installfiles')) {
        Move-VerifiedProjectArtifact -Root $projectRoot -Source 'installfiles' -Destination "$snapshot\installfiles" -Purpose 'Previous release (user data will be moved back)'
    }
    Move-VerifiedProjectArtifact -Root $projectRoot -Source $staging -Destination 'installfiles' -Purpose 'Current release'
    foreach ($dataDirectory in @('data', 'reports')) {
        $previousData = Join-ProjectMaterialPath $projectRoot "$snapshot\installfiles\$dataDirectory"
        if (Test-Path -LiteralPath $previousData) {
            Move-VerifiedProjectArtifact -Root $projectRoot -Source $previousData -Destination "installfiles\$dataDirectory" -Purpose 'Preserve application data'
        }
    }
    Get-FileHash -LiteralPath (Join-ProjectMaterialPath $projectRoot 'installfiles\Hollylark.exe') -Algorithm SHA256
}
finally {
    Pop-Location
}
