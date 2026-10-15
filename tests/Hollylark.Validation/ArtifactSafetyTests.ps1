. (Join-Path $PSScriptRoot '..\..\project_materials.ps1')

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-ProjectMaterialPath $root 'tools\ArtifactSafety.ps1')
$fixture = 'Workspace\Validation\artifact-safety-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
$source = Join-ProjectMaterialPath $root "$fixture\source"
New-Item -ItemType Directory -Path "$source\nested" -Force | Out-Null
[IO.File]::WriteAllText("$source\nested\proof.txt", 'Integrity regression fixture.')
$hash = (Get-FileHash -LiteralPath "$source\nested\proof.txt").Hash
$checks = [Collections.Generic.List[string]]::new()
function Expect-Rejection {
    param([scriptblock]$Action, [string]$Name)
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw "Expected rejection: $Name" }
    $checks.Add($Name)
}
Expect-Rejection { Move-VerifiedProjectArtifact -Root $root -Source "$fixture\source" -Destination '..\outside-project' -Purpose 'Negative test' } 'Destination containment'
Expect-Rejection { Move-VerifiedProjectArtifact -Root $root -Source '..\outside-project' -Destination "$fixture\invalid" -Purpose 'Negative test' } 'Source containment'
Expect-Rejection { Move-VerifiedProjectArtifact -Root $root -Source "$fixture\source" -Destination "$fixture\source\child" -Purpose 'Negative test' } 'Nested destination rejected'
Expect-Rejection { Join-ProjectMaterialPath $root 'Workspace\..\OtherProject' } 'Central category traversal rejected'
$otherProject = Join-Path (Split-Path -Parent (Get-ProjectMaterialRoot -ProjectRoot $root -Category Workspace)) 'OtherProject\artifact'
Expect-Rejection { Move-VerifiedProjectArtifact -Root $root -Source "$fixture\source" -Destination $otherProject -Purpose 'Negative test' } 'Other central project rejected'
$handle = [IO.File]::Open("$source\nested\proof.txt", [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    Expect-Rejection { Move-VerifiedProjectArtifact -Root $root -Source "$fixture\source" -Destination "$fixture\locked" -Purpose 'Negative test' } 'In-use file rejected before move'
}
finally { $handle.Dispose() }
if (-not (Test-Path -LiteralPath "$source\nested\proof.txt")) { throw 'Rejected operation altered source.' }
$checks.Add('Rejected operations preserve source')
Move-VerifiedProjectArtifact -Root $root -Source "$fixture\source" -Destination "$fixture\moved" -Purpose 'Verified regression fixture move'
if ((Test-Path -LiteralPath $source) -or (Get-FileHash -LiteralPath (Join-ProjectMaterialPath $root "$fixture\moved\nested\proof.txt")).Hash -ne $hash) {
    throw 'Successful move lost data or left a duplicate.'
}
$checks.Add('Successful move preserves nested content and creates no duplicate')
New-Item -ItemType Directory -Path $source -Force | Out-Null
[IO.File]::WriteAllText("$source\second.txt", 'A separate collision fixture.')
Expect-Rejection { Move-VerifiedProjectArtifact -Root $root -Source "$fixture\source" -Destination "$fixture\moved" -Purpose 'Negative test' } 'Existing archive cannot be overwritten'
if (-not (Test-Path -LiteralPath "$source\second.txt")) { throw 'Collision altered source.' }
@{ Time = (Get-Date).ToString('o'); Checks = $checks; Fixture = $fixture } | ConvertTo-Json | Set-Content -LiteralPath (Join-ProjectMaterialPath $root "$fixture\results.json") -Encoding UTF8
Write-Output "PASS: $($checks.Count) artifact safety checks. Fixtures retained in $fixture"
