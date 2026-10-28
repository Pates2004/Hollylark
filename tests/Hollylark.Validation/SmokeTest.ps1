param(
    [string]$ReportSubdirectory = ('release-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')),
    [ValidateSet('Cancel', 'Close', 'AltF4', 'Escape', 'Continue')][string]$FirstRunAction = 'Cancel',
    [ValidateSet('pl', 'en')][string]$InterfaceLanguage = 'pl',
    [string]$RuntimeConfigPath
)
. (Join-Path $PSScriptRoot '..\..\project_materials.ps1')

$ErrorActionPreference = 'Stop'
$InterfaceLanguage = $InterfaceLanguage.ToLowerInvariant()
$languageNameKey = if ($InterfaceLanguage -eq 'pl') { 'Settings.Language.Polish' } else { 'Settings.Language.English' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$executable = Join-ProjectMaterialPath $projectRoot 'installfiles\Hollylark.exe'
$prismLibrary = Join-Path (Split-Path -Parent $executable) 'prism.dll'
if (-not (Test-Path -LiteralPath $prismLibrary) -or
    (Get-FileHash -LiteralPath $prismLibrary -Algorithm SHA256).Hash -ne '99A73CD24AC777EDE4A5742E62CC3615EC7F5722862C0D9B5E4B3672DCF46CC4') {
    throw 'Current release requires the pinned Prismatoid 0.4.0 x64 prism.dll beside Hollylark.exe.'
}
if (Get-Process | Where-Object { try { $_.Path -eq $executable } catch { $false } }) {
    throw 'Close the existing Hollylark instance before validating the release; user processes are not stopped.'
}
$settingsFile = Join-ProjectMaterialPath $projectRoot 'installfiles\data\settings.json'
$needsSetup = $true
$initialInterfaceLanguage = 'pl'
$languagePropertyPresent = $false
$showHints = $true
$showOperationMessages = $true
$confirmExit = $true
$valueDisplayMode = 'StaticText'
$gainControlMode = 'Choice'
if (Test-Path -LiteralPath $settingsFile) {
    $preferences = Get-Content -LiteralPath $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -ne $preferences.PSObject.Properties['ShowHints']) { $showHints = $preferences.ShowHints }
    if ($null -ne $preferences.PSObject.Properties['ShowOperationMessages']) { $showOperationMessages = $preferences.ShowOperationMessages }
    if ($null -ne $preferences.PSObject.Properties['ConfirmExit']) { $confirmExit = $preferences.ConfirmExit }
    if ($null -ne $preferences.PSObject.Properties['ValueDisplayMode']) { $valueDisplayMode = $preferences.ValueDisplayMode }
    if ($null -ne $preferences.PSObject.Properties['GainControlMode']) { $gainControlMode = $preferences.GainControlMode }
    $needsSetup = -not $preferences.FirstRunCompleted
    if ($preferences.StartWithWindows -or $preferences.CloseToTray) {
        throw 'Smoke test requires Windows startup and close-to-tray disabled. Automatic USB reads and native exit confirmation are supported; existing user preferences are not modified.'
    }
    if ($null -ne $preferences.PSObject.Properties['Language']) {
        if ($preferences.Language -cnotin @('pl', 'en')) { throw 'Existing preferences contain an unsupported language; no configuration is changed.' }
        $initialInterfaceLanguage = $preferences.Language
        $languagePropertyPresent = $true
    }
}
if (-not $needsSetup -and $initialInterfaceLanguage -cne $InterfaceLanguage) {
    throw "Existing preferences select '$initialInterfaceLanguage', not '$InterfaceLanguage'. Choose the matching InterfaceLanguage; user configuration is never rewritten for a smoke test."
}
$settingsHash = if (Test-Path -LiteralPath $settingsFile) { (Get-FileHash -LiteralPath $settingsFile).Hash } else { $null }
if ($needsSetup -and $FirstRunAction -eq 'Continue' -and $null -ne $settingsHash) {
    throw 'Continue smoke requires an absent configuration; existing user preferences are never changed.'
}
$dataDirectoryExisted = Test-Path -LiteralPath (Split-Path -Parent $settingsFile)
$validationRoot = [IO.Path]::GetFullPath((Join-ProjectMaterialPath $projectRoot 'Workspace\Validation')).TrimEnd('\') + '\'
$reportDirectory = [IO.Path]::GetFullPath((Join-Path $validationRoot $ReportSubdirectory))
if (-not $reportDirectory.StartsWith($validationRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Smoke-test reports must stay inside Workspace\Validation.'
}
if (Test-Path -LiteralPath $reportDirectory) { throw 'Choose a new smoke-test report directory to preserve previous evidence.' }
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$reportPath = Join-Path $reportDirectory 'Hollylark-release-smoke.json'
$languageCatalogs = @{}
$captionSources = [ordered]@{}
$externalLanguageCatalogs = @(foreach ($language in @('pl', 'en')) {
    $path = Join-ProjectMaterialPath $projectRoot ('installfiles\langs\' + $language + '.lng')
    $present = Test-Path -LiteralPath $path -PathType Leaf
    [pscustomobject][ordered]@{
        Language = $language
        Path = $path
        Present = $present
        SHA256 = if ($present) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { $null }
        Bytes = if ($present) { (Get-Item -LiteralPath $path).Length } else { $null }
    }
})
try {
    foreach ($language in @('pl', 'en')) {
        $publishedPath = Join-ProjectMaterialPath $projectRoot ('installfiles\langs\' + $language + '.lng')
        if (-not (Test-Path -LiteralPath $publishedPath -PathType Leaf)) { throw "Published language catalog is missing: $publishedPath" }
        $sources = @($publishedPath)
        $captionSources[$language] = @($sources)
        $entries = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
        foreach ($source in $sources) {
            $document = [IO.File]::ReadAllText($source, [Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json
            if ($document -isnot [pscustomobject]) { throw "Expected a JSON key:string caption catalog: $source" }
            foreach ($property in $document.PSObject.Properties) {
                if ($property.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($property.Value) -or $entries.ContainsKey($property.Name)) {
                    throw "Invalid or duplicate caption key '$($property.Name)' in $source"
                }
                $entries.Add($property.Name, $property.Value)
            }
        }
        if ($entries.Count -eq 0) { throw "Empty caption catalog for '$language'." }
        $languageCatalogs[$language] = $entries
    }
    if ($languageCatalogs['pl'].Count -ne $languageCatalogs['en'].Count) { throw 'Polish and English caption catalog key counts differ.' }
    foreach ($key in $languageCatalogs['pl'].Keys) {
        if (-not $languageCatalogs['en'].ContainsKey($key)) { throw "English caption catalog is missing '$key'." }
    }
    if (@($externalLanguageCatalogs | Where-Object { -not $_.Present }).Count -ne 0) {
        throw 'The release must contain both external catalogs: installfiles\langs\pl.lng and installfiles\langs\en.lng. The executable was not started.'
    }
}
catch {
    [ordered]@{
        Time = (Get-Date).ToString('o'); Executable = $executable
        InterfaceLanguage = $InterfaceLanguage; FirstRunAction = $FirstRunAction
        Stage = 'LanguageCatalogPreflight'; Passed = $false; ApplicationStarted = $false
        ExternalLanguageCatalogs = $externalLanguageCatalogs; CaptionSources = $captionSources
        Error = $_.Exception.Message
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    throw
}
function Get-ReleaseText {
    param([string]$Key, [ValidateSet('pl', 'en')][string]$Language = $InterfaceLanguage)
    if (-not $languageCatalogs[$Language].ContainsKey($Key)) { throw "Missing smoke caption '$Key' for '$Language'." }
    return $languageCatalogs[$Language][$Key]
}
function Assert-ReleaseCatalogsUnchanged {
    foreach ($catalog in $externalLanguageCatalogs) {
        if (-not (Test-Path -LiteralPath $catalog.Path -PathType Leaf) -or (Get-FileHash -LiteralPath $catalog.Path -Algorithm SHA256).Hash -ne $catalog.SHA256) {
            throw "Published language catalog changed during the smoke test: $($catalog.Path)"
        }
    }
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class HollylarkWindowProbe {
    public delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr window, int identifier);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr first, IntPtr second);
    public static string Text(IntPtr window) {
        var text = new StringBuilder(8192);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }
    public static string ClassName(IntPtr window) {
        var text = new StringBuilder(256);
        GetClassName(window, text, text.Capacity);
        return text.ToString();
    }
    public static IntPtr FindDialog(uint process) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, parameter) => {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner == process && IsWindowVisible(window) && ClassName(window) == "#32770") {
                found = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static IntPtr Find(uint process) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, parameter) => {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner == process) {
                var title = new StringBuilder(512);
                GetWindowText(window, title, title.Capacity);
                if (title.ToString().StartsWith("Hollylark") && title.ToString().EndsWith("LARK A1")) { found = window; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@
$bytes = [IO.File]::ReadAllBytes($executable)
$peOffset = [BitConverter]::ToInt32($bytes, 60)
$machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
$subsystem = [BitConverter]::ToUInt16($bytes, $peOffset + 24 + 68)
if ($machine -ne 0x8664 -or $subsystem -ne 2) { throw 'Expected native x64 GUI apphost.' }
$runtimePath = if ([string]::IsNullOrWhiteSpace($RuntimeConfigPath)) {
    Join-ProjectMaterialPath $projectRoot 'Workspace\Build\bin\LarkControl.Windows\Release\net10.0-windows\win-x64\Hollylark.runtimeconfig.json'
} else {
    [IO.Path]::GetFullPath($RuntimeConfigPath)
}
if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
    throw 'Matching publication runtime metadata is missing. Publish first or pass -RuntimeConfigPath with the preserved publication build path.'
}
$runtime = Get-Content -LiteralPath $runtimePath -Raw | ConvertFrom-Json
$frameworkNames = @($runtime.runtimeOptions.frameworks | ForEach-Object { $_.name })
if ($runtime.runtimeOptions.tfm -ne 'net10.0' -or $frameworkNames -notcontains 'Microsoft.NETCore.App' -or
    $frameworkNames -notcontains 'Microsoft.WindowsDesktop.App' -or $runtime.runtimeOptions.includedFrameworks -or
    @($runtime.runtimeOptions.frameworks | Where-Object { $_.version -notmatch '^10\.' }).Count -gt 0) {
    throw 'Expected a framework-dependent .NET 10 Windows Desktop publication configuration.'
}
. (Join-ProjectMaterialPath $projectRoot 'tools\ArtifactSafety.ps1')
function Send-ReleaseEscape {
    param([IntPtr]$WindowHandle, [Windows.Automation.AutomationElement]$FocusTarget, [int]$ProcessId)
    $FocusTarget.SetFocus()
    for ($attempt = 0; $attempt -lt 30 -and [Windows.Automation.AutomationElement]::FocusedElement.Current.ProcessId -ne $ProcessId; $attempt++) {
        Start-Sleep -Milliseconds 100
    }
    if ([Windows.Automation.AutomationElement]::FocusedElement.Current.ProcessId -ne $ProcessId) {
        throw 'Window-directed Escape requires keyboard focus in the tested application.'
    }
    [void][HollylarkWindowProbe]::PostMessage($WindowHandle, 0x0100, [IntPtr]0x1B, [IntPtr]1)
    [void][HollylarkWindowProbe]::PostMessage($WindowHandle, 0x0101, [IntPtr]0x1B, [IntPtr]0xC0010001L)
    Start-Sleep -Milliseconds 500
}

function Get-ReleaseElement {
    param([Windows.Automation.AutomationElement]$Root, [string]$Identifier)
    $element = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Identifier))
    $name = if ($null -ne $element) { $element.Current.Name } else { $null }
    if ($null -eq $element -or [string]::IsNullOrWhiteSpace($name)) {
        $failure = [ordered]@{
            RequestedIdentifier = $Identifier
            FirstMatchMissing = $null -eq $element
            FirstMatchName = $name
            RootProcessId = $Root.Current.ProcessId
            RootIdentifier = $Root.Current.AutomationId
            Elements = @()
        }
        try {
            $elements = $Root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
            $failure.Elements = @($elements | ForEach-Object {
                $current = $_.Current
                if ($current.AutomationId -or $current.ControlType -eq [Windows.Automation.ControlType]::TabItem) {
                    $selection = $null
                    $selected = if ($_.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selection)) {
                        $selection.Current.IsSelected
                    } else { $null }
                    [ordered]@{
                        Identifier = $current.AutomationId; Name = $current.Name; Class = $current.ClassName
                        Framework = $current.FrameworkId; Type = $current.ControlType.ProgrammaticName
                        Selected = $selected; Offscreen = $current.IsOffscreen; RuntimeId = $_.GetRuntimeId()
                    }
                }
            })
        }
        catch { $failure.CaptureError = $_.ToString() }
        try {
            $failure | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath (
                Join-Path $reportDirectory ('uia-missing-control-' + [guid]::NewGuid().ToString('N') + '.json'))
        }
        catch { Write-Warning ('Could not retain UI Automation failure details: ' + $_.ToString()) }
        throw "Published control is missing or unnamed: $Identifier"
    }
    return $element
}
function Get-ReleaseCaptionElement {
    param([Windows.Automation.AutomationElement]$Root, [string]$Identifier, [string]$Key, [string]$Language = $InterfaceLanguage)
    $element = Get-ReleaseElement -Root $Root -Identifier $Identifier
    if ($element.Current.Name -cne (Get-ReleaseText -Key $Key -Language $Language)) {
        throw "Unexpected '$Language' caption for '$Identifier': $($element.Current.Name)"
    }
    return $element
}
function Get-ReleaseUsbReadState {
    param([Windows.Automation.AutomationElement]$Root)
    $readout = Get-ReleaseElement -Root $Root -Identifier 'ConnectionReadout'
    $text = $readout.Current.Name
    foreach ($state in @('Ready', 'Reading', 'Unavailable', 'None', 'Choose', 'Waiting')) {
        $pattern = [regex]::Escape((Get-ReleaseText ('Main.Usb.' + $state)))
        foreach ($index in 0..2) {
            $pattern = $pattern.Replace([regex]::Escape('{' + $index + '}'), '.+')
        }
        if ([regex]::IsMatch($text, '\A' + $pattern + '\z')) {
            return [pscustomobject]@{ State = $state; Confirmed = $state -eq 'Ready'; Text = $text }
        }
    }
    throw "Published USB status does not match the selected language catalog: $text"
}
function Assert-ReleaseStaticReadout {
    param([Windows.Automation.AutomationElement]$Element, [string]$ExpectedName)
    $valuePattern = $null
    if ($Element.Current.ControlType -ne [Windows.Automation.ControlType]::Text -or
        -not $Element.Current.IsKeyboardFocusable -or $Element.Current.Name -cne $ExpectedName -or
        $Element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)) {
        throw "Expected a focusable native Text readout with its actual content and no edit pattern: $($Element.Current.AutomationId)"
    }
}
function Assert-ReleaseInformationalEdits {
    param([Windows.Automation.AutomationElement]$Root, [string[]]$AllowedIdentifiers = @())
    $edits = $Root.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit))
    foreach ($edit in $edits) {
        $valuePattern = $null
        if ($edit.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern) -and $valuePattern.Current.IsReadOnly) {
            if ($edit.Current.AutomationId -cnotin $AllowedIdentifiers -or -not $edit.Current.IsKeyboardFocusable -or
                [string]::IsNullOrWhiteSpace($edit.Current.Name) -or [string]::IsNullOrWhiteSpace($valuePattern.Current.Value)) {
                throw "Unexpected, unnamed or unreadable informational edit field: $($edit.Current.AutomationId)"
            }
        }
    }
}
function Close-ReleaseMain {
    param([Diagnostics.Process]$OwnedProcess, [IntPtr]$WindowHandle, [bool]$RequireConfirmation, [switch]$RejectOnce)
    if ($OwnedProcess.HasExited) { throw 'The smoke-owned process exited before normal-close verification.' }
    $ownedRoot = [Windows.Automation.AutomationElement]::FromHandle($WindowHandle)
    if ($ownedRoot.Current.ProcessId -ne $OwnedProcess.Id -or $ownedRoot.Current.AutomationId -ne 'HollylarkWindow') {
        throw 'Refusing to close a window that is not the main window of this smoke-owned process.'
    }
    $messages = @()
    $confirmationAccess = @()
    $rejected = $false
    $approved = $false
    [void][HollylarkWindowProbe]::PostMessage($WindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    for ($attempt = 0; $attempt -lt 100 -and -not $OwnedProcess.HasExited; $attempt++) {
        Start-Sleep -Milliseconds 100
        $dialogHandle = [HollylarkWindowProbe]::FindDialog($OwnedProcess.Id)
        if ($dialogHandle -eq [IntPtr]::Zero) { continue }
        $dialog = [Windows.Automation.AutomationElement]::FromHandle($dialogHandle)
        if ($approved) { throw 'An unexpected additional confirmation appeared after approving native exit.' }
        if ($dialog.Current.Name -cne (Get-ReleaseText 'Main.Exit.Title')) { throw 'Refusing an unexpected native dialog during smoke-owned exit.' }
        $knownMessages = @((Get-ReleaseText 'Main.Exit.Confirmation'), (Get-ReleaseText 'Main.Exit.BusyConfirmation'))
        $message = [HollylarkWindowProbe]::Text([HollylarkWindowProbe]::GetDlgItem($dialogHandle, 65535))
        if ($message -cnotin $knownMessages) { throw 'Native exit dialog did not expose the exact localized confirmation; no response was injected.' }
        $messages += $message
        $buttonId = if ($RejectOnce -and -not $rejected) { '7' } else { '6' }
        $button = $dialog.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $buttonId))
        if ($null -ne $button -and $button.Current.ControlType -eq [Windows.Automation.ControlType]::Button) {
            $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            $confirmationAccess += 'UIA InvokePattern'
        } else {
            $buttonHandle = [HollylarkWindowProbe]::GetDlgItem($dialogHandle, [int]$buttonId)
            if ($buttonHandle -eq [IntPtr]::Zero -or [HollylarkWindowProbe]::ClassName($buttonHandle) -cne 'Button' -or
                -not [HollylarkWindowProbe]::PostMessage($buttonHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)) {
                throw 'Native Yes/No button is missing or could not be pressed; confirmation was not bypassed.'
            }
            $confirmationAccess += 'Win32 BM_CLICK; managed native UIA proxy unavailable'
        }
        if ($buttonId -eq '7') {
            Start-Sleep -Milliseconds 300
            if ($OwnedProcess.HasExited -or [HollylarkWindowProbe]::Find($OwnedProcess.Id) -ne $WindowHandle) {
                throw 'Declining native exit did not preserve the smoke-owned main window.'
            }
            $rejected = $true
            [void][HollylarkWindowProbe]::PostMessage($WindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        } else {
            $approved = $true
            [void]$OwnedProcess.WaitForExit(5000)
            break
        }
    }
    if (-not $OwnedProcess.HasExited) { throw 'Application did not close normally; the smoke never kills it or disables confirmation.' }
    if ($RequireConfirmation -and (-not $approved -or ($RejectOnce -and -not $rejected))) {
        throw 'ConfirmExit=true did not exercise the expected native confirmation responses.'
    }
    if ($OwnedProcess.ExitCode -ne 0) { throw "Unexpected normal-exit code: $($OwnedProcess.ExitCode)" }
    return [pscustomobject]@{ Required = $RequireConfirmation; NativeYesInvoked = $approved; NativeNoKeptWindowOpen = $rejected; Messages = $messages; AccessMethods = $confirmationAccess }
}
function Select-ReleaseChoice {
    param([Windows.Automation.AutomationElement]$Control, [string]$Caption)
    $expansion = $Control.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expansion.Expand()
    try {
        $condition = [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $Caption),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem))
        $choice = $Control.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -eq $choice) { throw "Choice does not expose its user-facing caption: $Caption" }
        $choice.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }
    finally { $expansion.Collapse() }
    $selection = $Control.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($selection.Count -ne 1 -or $selection[0].Current.Name -cne $Caption) { throw "Published choice was not selected: $Caption" }
}
function Assert-ReleaseMainLanguage {
    param([Windows.Automation.AutomationElement]$Root)
    if ($Root.Current.AutomationId -ne 'HollylarkWindow' -or $Root.Current.Name -cne (Get-ReleaseText 'Main.Window.Title')) {
        throw 'Unexpected main window identity or caption.'
    }
    foreach ($key in @('Main.Menu.ProgramName', 'Main.Menu.HelpName', 'Main.Receiver.TabName')) {
        $element = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, (Get-ReleaseText $key)))
        if ($null -eq $element) { throw "Published main UI does not use '$InterfaceLanguage': $key" }
    }
}
function Restore-GeneratedSetup {
    if (-not $setupCompleted -or $null -eq $generatedSettingsHash -or -not (Test-Path -LiteralPath $settingsFile)) { return }
    if (-not $process.HasExited) { throw 'Cannot move generated setup evidence while the application is running.' }
    if (Get-Process | Where-Object { try { $_.Path -eq $executable } catch { $false } }) {
        throw 'Another Hollylark instance is running; generated configuration is left in place to prevent interference.'
    }
    if ((Get-FileHash -LiteralPath $settingsFile).Hash -ne $generatedSettingsHash) {
        throw 'Generated settings changed unexpectedly; left in place to avoid moving user edits.'
    }
    $destination = [IO.Path]::Combine('Workspace\Validation', $ReportSubdirectory, 'published-setup-profile\data')
    if ($dataDirectoryExisted) {
        Move-VerifiedProjectArtifact -Root $projectRoot -Source 'installfiles\data\settings.json' -Destination ($destination + '\settings.json') -Purpose 'Preserve setup smoke fixture and restore absent original settings'
    }
    else {
        $dataDirectory = Split-Path -Parent $settingsFile
        $dataFiles = @(Get-ChildItem -LiteralPath $dataDirectory -Force -Recurse)
        if ($dataFiles.Count -ne 1 -or $dataFiles[0].FullName -ne $settingsFile) {
            throw 'Unexpected additional application data; no directory move attempted.'
        }
        Move-VerifiedProjectArtifact -Root $projectRoot -Source 'installfiles\data' -Destination $destination -Purpose 'Preserve generated setup profile and restore the original data-free release directory'
    }
}
function Assert-ReleaseSingleInstance {
    param(
        [Diagnostics.Process]$Owner,
        [Windows.Automation.AutomationElement]$Window,
        [string]$Stage,
        [string]$ProbeExecutable = $executable,
        [switch]$Minimize
    )
    $windowPattern = $Window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)
    if ($Minimize) { $windowPattern.SetWindowVisualState([Windows.Automation.WindowVisualState]::Minimized) }
    $duplicate = Start-Process -FilePath $ProbeExecutable -WindowStyle Hidden -PassThru
    try {
        if (-not $duplicate.WaitForExit(5000) -or $duplicate.ExitCode -ne 0) {
            throw "The duplicate launch did not exit successfully: $Stage"
        }
        if ($Owner.HasExited -or $Window.Current.ProcessId -ne $Owner.Id) {
            throw "The duplicate launch replaced or closed the original owner: $Stage"
        }
        if ($Minimize) {
            for ($attempt = 0; $attempt -lt 50 -and
                $windowPattern.Current.WindowVisualState -eq [Windows.Automation.WindowVisualState]::Minimized; $attempt++) {
                Start-Sleep -Milliseconds 100
            }
            if ($windowPattern.Current.WindowVisualState -eq [Windows.Automation.WindowVisualState]::Minimized) {
                throw "The duplicate launch did not restore the minimized window: $Stage"
            }
        }
        return [ordered]@{ Stage=$Stage; OwnerProcessId=$Owner.Id; DuplicateProcessId=$duplicate.Id;
            ProbeExecutable=$ProbeExecutable; DuplicateExitCode=$duplicate.ExitCode; OwnerPreserved=$true; RestoredAfterMinimize=[bool]$Minimize }
    }
    finally {
        if (-not $duplicate.HasExited) {
            $duplicate.Kill()
            [void]$duplicate.WaitForExit(5000)
        }
        $duplicate.Dispose()
    }
}
function Assert-ReleaseCommandErrorLanguage {
    $probe = Start-Process -FilePath $executable -ArgumentList '--unsupported-option' -WindowStyle Hidden -PassThru
    try {
        $dialog = $null
        for ($attempt = 0; $attempt -lt 80 -and $null -eq $dialog; $attempt++) {
            Start-Sleep -Milliseconds 100
            if ($probe.HasExited) { throw 'Invalid command exited without its localized error dialog.' }
            $dialogHandle = [HollylarkWindowProbe]::FindDialog($probe.Id)
            if ($dialogHandle -ne [IntPtr]::Zero) { $dialog = [Windows.Automation.AutomationElement]::FromHandle($dialogHandle) }
        }
        if ($null -eq $dialog) { throw 'Invalid command did not display its error dialog.' }
        $expected = [string]::Format((Get-ReleaseText 'Startup.Failed'), (Get-ReleaseText 'Startup.CommandUsage'))
        $message = [HollylarkWindowProbe]::Text([HollylarkWindowProbe]::GetDlgItem($dialogHandle, 65535))
        if ($message -cne $expected) { throw 'Command-line error did not respect the saved language.' }
        if (-not $probe.CloseMainWindow()) { throw 'Command-line error could not be closed through its native window.' }
        if (-not $probe.WaitForExit(5000) -or $probe.ExitCode -ne 1) { throw 'Invalid command did not exit with failure status.' }
    }
    finally {
        if (-not $probe.HasExited) { [void]$probe.CloseMainWindow(); [void]$probe.WaitForExit(5000) }
        $probe.Dispose()
    }
}
$beforeLaunchHash = if (Test-Path -LiteralPath $settingsFile) { (Get-FileHash -LiteralPath $settingsFile).Hash } else { $null }
if ($beforeLaunchHash -ne $settingsHash) { throw 'Configuration changed during preflight; the application was not started.' }
Assert-ReleaseCatalogsUnchanged
$process = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
$singleInstanceChecks = [Collections.Generic.List[object]]::new()
$setupCompleted = $false
$generatedSettingsHash = $null
try {
    if ($needsSetup) {
        $firstRunCondition = [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'FirstRunWindow'))
        $firstRun = $null
        for ($attempt = 0; $attempt -lt 80 -and $null -eq $firstRun; $attempt++) {
            Start-Sleep -Milliseconds 100
            if ($process.HasExited) { throw 'Application exited before offering first-run setup.' }
            if ([HollylarkWindowProbe]::Find($process.Id) -ne [IntPtr]::Zero) { throw 'Main window was created before first-run setup completed.' }
            $firstRun = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $firstRunCondition)
        }
        if ($null -eq $firstRun) { throw 'First-run setup was not offered before the main window.' }
        $singleInstanceChecks.Add((Assert-ReleaseSingleInstance -Owner $process -Window $firstRun -Stage 'FirstRun'))
        if ($firstRun.Current.Name -cne (Get-ReleaseText -Key 'Setup.Title' -Language $initialInterfaceLanguage)) {
            throw 'The initial wizard title does not match its startup language.'
        }
        $continue = Get-ReleaseCaptionElement -Root $firstRun -Identifier 'SaveFirstRunButton' -Key 'Setup.Continue.Name' -Language $initialInterfaceLanguage
        $languageChoice = Get-ReleaseCaptionElement -Root $firstRun -Identifier 'FirstRunLanguageChoice' -Key 'Setup.Language.Name' -Language $initialInterfaceLanguage
        $theme = Get-ReleaseCaptionElement -Root $firstRun -Identifier 'FirstRunThemeChoice' -Key 'Setup.Theme.Name' -Language $initialInterfaceLanguage
        if ($continue.Current.IsEnabled -or $languageChoice.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection().Count -ne 0 -or
            $theme.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection().Count -ne 0) {
            throw 'Continue must require explicit language and theme choices, with neither choice initially selected.'
        }
        Select-ReleaseChoice -Control $languageChoice -Caption (Get-ReleaseText $languageNameKey)
        for ($attempt = 0; $attempt -lt 50 -and $firstRun.Current.Name -cne (Get-ReleaseText 'Setup.Title'); $attempt++) { Start-Sleep -Milliseconds 100 }
        if ($firstRun.Current.Name -cne (Get-ReleaseText 'Setup.Title')) { throw 'The wizard language preview did not update its title.' }
        $continue = Get-ReleaseCaptionElement -Root $firstRun -Identifier 'SaveFirstRunButton' -Key 'Setup.Continue.Name'
        $theme = Get-ReleaseCaptionElement -Root $firstRun -Identifier 'FirstRunThemeChoice' -Key 'Setup.Theme.Name'
        $cancel = Get-ReleaseCaptionElement -Root $firstRun -Identifier 'CancelFirstRunButton' -Key 'Setup.Cancel.Name'
        if ($showHints) { [void](Get-ReleaseCaptionElement -Root $firstRun -Identifier 'FirstRunScope' -Key 'Setup.Scope') }
        Assert-ReleaseInformationalEdits -Root $firstRun
        $setupStatus = $firstRun.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'SectionResultReadout'))
        if ($continue.Current.IsEnabled -or ($null -ne $setupStatus -and -not $setupStatus.Current.IsOffscreen)) {
            throw 'Choosing only a language must keep Continue disabled without an instructional result readout.'
        }
        if ([HollylarkWindowProbe]::Find($process.Id) -ne [IntPtr]::Zero) { throw 'Language preview prematurely entered the main application.' }
        $afterPreviewHash = if (Test-Path -LiteralPath $settingsFile) { (Get-FileHash -LiteralPath $settingsFile).Hash } else { $null }
        if ($afterPreviewHash -ne $settingsHash) { throw 'Language preview changed the persisted configuration.' }
        if ($FirstRunAction -eq 'Continue') {
            Select-ReleaseChoice -Control $theme -Caption (Get-ReleaseText 'Settings.Theme.Light')
            if (-not $continue.Current.IsEnabled) { throw 'Choosing a language and then a theme did not enable Continue.' }
            if (Test-Path -LiteralPath $settingsFile) { throw 'Configuration appeared during setup; Continue was not invoked to protect user data.' }
            $continue.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            $setupCompleted = $true
            for ($attempt = 0; $attempt -lt 50 -and -not (Test-Path -LiteralPath $settingsFile); $attempt++) { Start-Sleep -Milliseconds 100 }
            if (-not (Test-Path -LiteralPath $settingsFile)) { throw 'Continue did not persist setup.' }
            $generatedSettingsHash = (Get-FileHash -LiteralPath $settingsFile).Hash
            $saved = Get-Content -LiteralPath $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
            if (-not $saved.FirstRunCompleted -or $saved.Language -cne $InterfaceLanguage -or $saved.Theme -ne 'Light' -or $saved.StartWithWindows -or $saved.CloseToTray -or -not $saved.ConfirmExit -or $saved.ValueDisplayMode -ne 'StaticText') {
                throw "Continue saved unexpected setup: language=$($saved.Language), theme=$($saved.Theme), completed=$($saved.FirstRunCompleted), startup=$($saved.StartWithWindows), tray=$($saved.CloseToTray), confirmExit=$($saved.ConfirmExit)."
            }
        }
        else {
            $setupHandle = [IntPtr]$firstRun.Current.NativeWindowHandle
            switch ($FirstRunAction) {
                'Cancel' {
                    $cancel.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
                }
                'Close' { $firstRun.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
                'AltF4' { [void][HollylarkWindowProbe]::PostMessage($setupHandle, 0x0112, [IntPtr]0xF060, [IntPtr]::Zero) }
                'Escape' {
                    $selection = $languageChoice.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern)
                    $selectedLanguage = $selection.Current.GetSelection()[0].Current.Name
                    $dropdown = $languageChoice.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
                    $dropdown.Expand()
                    if ($dropdown.Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Expanded) { throw 'Setup dropdown did not open.' }
                    Send-ReleaseEscape -WindowHandle $setupHandle -FocusTarget $languageChoice -ProcessId $process.Id
                    if ($process.HasExited -or $dropdown.Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Collapsed -or
                        $selection.Current.GetSelection()[0].Current.Name -cne $selectedLanguage) {
                        throw 'Escape must close only the setup dropdown and preserve selection.'
                    }
                    Send-ReleaseEscape -WindowHandle $setupHandle -FocusTarget $languageChoice -ProcessId $process.Id
                    if ($process.HasExited -or $firstRun.Current.NativeWindowHandle -ne $setupHandle.ToInt64() -or
                        [HollylarkWindowProbe]::Find($process.Id) -ne [IntPtr]::Zero) {
                        throw 'Escape must leave first-run open without opening main or exiting the application.'
                    }
                    $afterEscapeHash = if (Test-Path -LiteralPath $settingsFile) { (Get-FileHash -LiteralPath $settingsFile).Hash } else { $null }
                    if ($afterEscapeHash -ne $settingsHash) { throw 'Escape changed the setup configuration.' }
                    $cancel.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
                }
            }
            for ($attempt = 0; $attempt -lt 50 -and -not $process.HasExited; $attempt++) {
                if ([HollylarkWindowProbe]::Find($process.Id) -ne [IntPtr]::Zero) { throw 'Cancelling setup entered the main application.' }
                Start-Sleep -Milliseconds 100
            }
            if (-not $process.HasExited -or $process.ExitCode -ne 0) { throw 'Cancelling setup did not terminate the application normally.' }
            $afterCancelHash = if (Test-Path -LiteralPath $settingsFile) { (Get-FileHash -LiteralPath $settingsFile).Hash } else { $null }
            if ($afterCancelHash -ne $settingsHash) { throw 'Cancelling setup changed the configuration.' }
            Assert-ReleaseCatalogsUnchanged
            $report = [ordered]@{
                Time = (Get-Date).ToString('o'); Executable = $executable
                SHA256 = (Get-FileHash -LiteralPath $executable).Hash; Bytes = $bytes.Length
                Version = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion
                NativeMachine = 'AMD64'; NativeSubsystem = 'Windows GUI'; FrameworkDependent = $true
                InterfaceLanguage = $InterfaceLanguage; InitialInterfaceLanguage = $initialInterfaceLanguage
                ExternalLanguageCatalogs = $externalLanguageCatalogs; CaptionSources = $captionSources
                LanguageCatalogsUnchanged = $true; FirstRunLanguagePreview = $true; FirstRunThemeSelected = $false
                FirstRunAction = $FirstRunAction; MainWindowCreated = $false; FirstRunCancelledWithoutSaving = $true
                EscapeKeepsSetupOpen = if ($FirstRunAction -eq 'Escape') { $true } else { $null }
                EscapeClosesOnlySetupDropdown = if ($FirstRunAction -eq 'Escape') { $true } else { $null }
                EscapePathCleanedUpByExplicitCancel = ($FirstRunAction -eq 'Escape')
                NormalExitCode = $process.ExitCode; SettingsWritten = $false
                SingleInstanceChecks = $singleInstanceChecks
                Limitations = 'Wizard language preview and cancellation checked; main UI and restart are not exercised on this path. AltF4 uses its native SC_CLOSE command; Escape is a window-directed key message. No global key injection, physical USB/audio, registry writes, or NVDA listening.'
            }
            $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
            $report | ConvertTo-Json -Depth 8
            return
        }
    }
    $window = [IntPtr]::Zero
    for ($attempt = 0; $attempt -lt 80 -and $window -eq [IntPtr]::Zero; $attempt++) {
        Start-Sleep -Milliseconds 100
        if ($process.HasExited) { throw "Application exited prematurely: $($process.ExitCode)" }
        $window = [HollylarkWindowProbe]::Find($process.Id)
    }
    if ($window -eq [IntPtr]::Zero) { throw 'Main window was not created.' }
    $automation = [Windows.Automation.AutomationElement]::FromHandle($window)
    if ($automation.Current.AutomationId -ne 'HollylarkWindow') { throw 'Unexpected window; could be an error dialog.' }
    $singleInstanceChecks.Add((Assert-ReleaseSingleInstance -Owner $process -Window $automation -Stage 'Main'))
    $singleInstanceChecks.Add((Assert-ReleaseSingleInstance -Owner $process -Window $automation -Stage 'MinimizedMain' -Minimize))
    $buildHost = Join-ProjectMaterialPath $projectRoot 'Workspace\Build\bin\LarkControl.Windows\Release\net10.0-windows\win-x64\Hollylark.exe'
    if (Test-Path -LiteralPath $buildHost) {
        $singleInstanceChecks.Add((Assert-ReleaseSingleInstance -Owner $process -Window $automation -Stage 'DifferentExecutablePath' -ProbeExecutable $buildHost))
    }
    Assert-ReleaseMainLanguage -Root $automation
    $informationalFields = @()
    if ($valueDisplayMode -eq 'ReadOnlyFields') {
        $informationalFields = @('Gain.Current', 'Noise.Current', 'Equalizer.Current', 'Reverb.Current',
            'ChannelMode.Current', 'Lights.Current', 'AutoOff.Current', 'Identification.Current')
        if ($gainControlMode -eq 'Buttons') { $informationalFields += 'Gain.DraftField' }
    }
    Assert-ReleaseInformationalEdits -Root $automation -AllowedIdentifiers $informationalFields
    $editorNames = [ordered]@{}
    foreach ($setting in @('Gain', 'Noise', 'Equalizer', 'Reverb', 'ChannelMode', 'Lights', 'AutoOff', 'Identification')) {
        if ($preferences.HiddenRows -contains $setting) { continue }
        if ($setting -eq 'Gain' -and $gainControlMode -eq 'Buttons') { continue }
        $identifier = if ($setting -eq 'Gain' -and $gainControlMode -eq 'Slider') { 'Gain.Slider' } else { $setting + '.Choice' }
        $editor = Get-ReleaseCaptionElement -Root $automation -Identifier $identifier -Key ('Setting.' + $setting + '.Name')
        $editorNames[$identifier] = $editor.Current.Name
    }
    $mainDataItems = $automation.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::DataItem))
    if ($mainDataItems.Count -ne 0) { throw 'Global setting controls must not expose DataItem containers.' }
    $microphoneScopeItems = @()
    if (@($preferences.HiddenRows).Count -eq 0) {
        $microphoneList = Get-ReleaseElement -Root $automation -Identifier 'TransmitterList'
        $microphoneItems = $microphoneList.FindAll([Windows.Automation.TreeScope]::Children,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem))
        $microphoneScopeItems = @($microphoneItems | ForEach-Object { [ordered]@{ Identifier=$_.Current.AutomationId; Name=$_.Current.Name } })
        if ($microphoneScopeItems.Count -notin @(3, 5) -or $microphoneScopeItems[-1].Identifier -cne 'AllTX.Item' -or
            -not $microphoneScopeItems[-1].Name.StartsWith((Get-ReleaseText 'Target.All.Name')) -or $microphoneScopeItems[-1].Name.Contains('%')) {
            throw 'Published microphone list must expose native individual items followed by All, without group battery telemetry.'
        }
    }
    $connectionReadout = Get-ReleaseElement -Root $automation -Identifier 'ConnectionReadout'
    Send-ReleaseEscape -WindowHandle $window -FocusTarget $connectionReadout -ProcessId $process.Id
    if ($process.HasExited -or [HollylarkWindowProbe]::Find($process.Id) -ne $window) { throw 'Escape closed or hid main.' }
    foreach ($identifier in @('ConnectionReadout', 'ReadoutStatus')) {
        $readout = Get-ReleaseElement -Root $automation -Identifier $identifier
        if ($readout.Current.ControlType -ne [Windows.Automation.ControlType]::Text -or -not $readout.Current.IsKeyboardFocusable) {
            throw "Published reference readout is not keyboard-readable static text: $identifier"
        }
    }
    $cancelOperation = Get-ReleaseElement -Root $automation -Identifier 'CancelOperationButton'
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        Start-Sleep -Milliseconds 100
        $initialUsbState = Get-ReleaseUsbReadState -Root $automation
        if ($attempt -ge 9 -and -not $cancelOperation.Current.IsEnabled -and $initialUsbState.State -ne 'Reading') { break }
    }
    if ($cancelOperation.Current.IsEnabled -or $initialUsbState.State -eq 'Reading') {
        throw 'Automatic USB reading did not settle. No cancellation, selection or hardware-setting change was forced.'
    }
    $receiverTab = $automation.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, (Get-ReleaseText 'Main.Receiver.TabName')))
    $receiverTab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $list = Get-ReleaseCaptionElement -Root $automation -Identifier 'ReceiverList' -Key 'Main.Receiver.DiscoveredName'
    if ($list.Current.ControlType -ne [Windows.Automation.ControlType]::List -or -not $list.Current.IsEnabled) {
        throw 'The physical receiver selector must remain an enabled native list even after automatic connection.'
    }
    $receiverLists = $receiverTab.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::List))
    if ($receiverLists.Count -ne 1) { throw 'The receiver tab must expose exactly one native receiver list.' }
    $activeReceiver = Get-ReleaseElement -Root $automation -Identifier 'ActiveReceiverReadout'
    if ($activeReceiver.Current.ControlType -ne [Windows.Automation.ControlType]::Text -or -not $activeReceiver.Current.IsKeyboardFocusable) {
        throw 'The active receiver status must be keyboard-readable static text.'
    }
    [void](Get-ReleaseCaptionElement -Root $automation -Identifier 'SearchButton' -Key 'Main.Receiver.SearchName')
    foreach ($identifier in @('SavedReceiverList', 'RememberedReceiverReadout', 'ConnectButton', 'ConnectAndRememberButton',
        'RememberReceiverButton', 'ForgetReceiverButton', 'DisconnectButton')) {
        $obsolete = $automation.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $identifier))
        if ($null -ne $obsolete) { throw "Obsolete manual receiver control is still present: $identifier" }
    }
    $programMenu = $automation.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, (Get-ReleaseText 'Main.Menu.ProgramName')))
    if ($null -eq $programMenu) { throw 'Program menu is missing.' }
    $programMenu.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $settingsCommand = $automation.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'SettingsMenuItem'))
    $settingsCommand.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $settingsWindow = $null
    $settingsCondition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'SettingsWindow'))
    for ($attempt = 0; $attempt -lt 50 -and $null -eq $settingsWindow; $attempt++) {
        Start-Sleep -Milliseconds 100
        $settingsWindow = $automation.FindFirst([Windows.Automation.TreeScope]::Descendants, $settingsCondition)
        if ($null -eq $settingsWindow) {
            $settingsWindow = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $settingsCondition)
        }
    }
    if ($null -eq $settingsWindow) { throw 'The settings window was not found in the release UI Automation tree.' }
    if ($settingsWindow.Current.Name -cne (Get-ReleaseText 'Settings.Title')) { throw 'The settings window title is not localized.' }
    $singleInstanceChecks.Add((Assert-ReleaseSingleInstance -Owner $process -Window $settingsWindow -Stage 'Settings'))
    $settingsLanguage = Get-ReleaseCaptionElement -Root $settingsWindow -Identifier 'SettingsLanguageChoice' -Key 'Settings.Language.Name'
    $selectedLanguage = $settingsLanguage.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($selectedLanguage.Count -ne 1 -or $selectedLanguage[0].Current.Name -cne (Get-ReleaseText $languageNameKey)) {
        throw 'Settings do not expose the persisted interface language using its native name.'
    }
    $restartNotice = $settingsWindow.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'SettingsLanguageRestartNotice'))
    if ($null -ne $restartNotice -and -not $restartNotice.Current.IsOffscreen) {
        throw 'An unchanged language must not add a redundant restart instruction to Settings.'
    }
    $categories = @('AppearanceSettingsTab', 'MainScreenSettingsTab', 'ControlsSettingsTab', 'StartupAndClosingSettingsTab', 'ProfilesSettingsTab', 'HotkeysSettingsTab')
    $categoryCaptionKeys = @{
        AppearanceSettingsTab = 'Settings.Appearance.Name'; MainScreenSettingsTab = 'Settings.MainScreen.Name'
        ControlsSettingsTab = 'Settings.Controls.Name'; StartupAndClosingSettingsTab = 'Settings.StartupAndClosing.Name'
        ProfilesSettingsTab = 'Settings.Profiles.Name'; HotkeysSettingsTab = 'Hotkeys.Tab.Name'
    }
    foreach ($identifier in $categories) {
        $category = $settingsWindow.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $identifier))
        if ($null -eq $category) { throw "Settings category missing from release: $identifier" }
        if ($category.Current.Name -cne (Get-ReleaseText $categoryCaptionKeys[$identifier])) { throw "Settings category is not localized: $identifier" }
        $category.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        if (-not $category.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) {
            throw "Published settings category could not be selected: $identifier"
        }
        if ($identifier -eq 'StartupAndClosingSettingsTab') {
            $startupToggles = $category.FindAll([Windows.Automation.TreeScope]::Descendants,
                [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::CheckBox))
            $startupToggleIds = @($startupToggles | ForEach-Object { $_.Current.AutomationId })
            if (($startupToggleIds -join ',') -cne 'StartWithWindowsCheckBox,CloseToTrayCheckBox,ConfirmExitCheckBox') {
                throw 'Startup and closing must share one category containing exactly the three lifecycle toggles.'
            }
            $usbHint = $settingsWindow.FindFirst([Windows.Automation.TreeScope]::Descendants,
                [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'ReceiverUsageHint'))
            if ($null -ne $usbHint) {
                throw 'Optional automatic USB explanations must be in Help, not a Settings readout.'
            }
        }
        Assert-ReleaseInformationalEdits -Root $settingsWindow
        if ($identifier -eq 'ControlsSettingsTab') {
            $autoApplyWarning = $settingsWindow.FindFirst([Windows.Automation.TreeScope]::Descendants,
                [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'AutoApplySelectionsWarning'))
            if ($null -ne $autoApplyWarning) { throw 'Automatic-apply instructions must not remain in Settings.' }
        }
        if ($identifier -eq 'HotkeysSettingsTab') {
            $actions = Get-ReleaseElement -Root $settingsWindow -Identifier 'HotkeyActionsList'
            $items = $actions.FindAll([Windows.Automation.TreeScope]::Descendants,
                [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem))
            $actionNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($item in $items) { [void]$actionNames.Add($item.Current.Name) }
            $scroll = $actions.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
            $initialScroll = $scroll.Current.VerticalScrollPercent
            try {
                if ($scroll.Current.VerticallyScrollable) {
                    $scroll.SetScrollPercent([Windows.Automation.ScrollPattern]::NoScroll, 100)
                    $deadline = [DateTime]::UtcNow.AddSeconds(3)
                    do {
                        $items = $actions.FindAll([Windows.Automation.TreeScope]::Descendants,
                            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem))
                        foreach ($item in $items) { [void]$actionNames.Add($item.Current.Name) }
                        if ($actionNames.Count -lt 13) { Start-Sleep -Milliseconds 30 }
                    } while ($actionNames.Count -lt 13 -and [DateTime]::UtcNow -lt $deadline)
                }
            }
            finally {
                if ($initialScroll -ge 0) { $scroll.SetScrollPercent([Windows.Automation.ScrollPattern]::NoScroll, $initialScroll) }
            }
            if ($actionNames.Count -ne 13 -or @($actionNames | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -like '*LarkControl.*' }).Count) {
                throw "Published virtualized shortcut list must expose thirteen named actions through scrolling. Found $($actionNames.Count): $($actionNames -join '; ')"
            }
            $shortcut = Get-ReleaseElement -Root $settingsWindow -Identifier 'HotkeyGestureTextBox'
            if ($shortcut.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.IsReadOnly) {
                throw 'The published shortcut field must be editable.'
            }
            [void](Get-ReleaseElement -Root $settingsWindow -Identifier 'CaptureHotkeyButton')
            $master = Get-ReleaseElement -Root $settingsWindow -Identifier 'EnableGlobalHotkeysCheckBox'
            $shortcutSettings = [ordered]@{
                ActionNames = @($actionNames)
                ActionCount = $actionNames.Count
                EditableShortcut = $true
                CaptureButtonPresent = $true
                Enabled = $master.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString()
            }
        }
    }
    $profilesCategory = Get-ReleaseElement -Root $settingsWindow -Identifier 'ProfilesSettingsTab'
    $profilesCategory.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Assert-ReleaseInformationalEdits -Root $settingsWindow
    $profileUsbState = Get-ReleaseUsbReadState -Root $automation
    if ($cancelOperation.Current.IsEnabled -or $profileUsbState.State -eq 'Reading') {
        throw 'USB state is changing during profile guard inspection. Retry after it settles; no hardware changes were made.'
    }
    $profiles = Get-ReleaseElement -Root $settingsWindow -Identifier 'AudioProfilesList'
    $profileSelection = $profiles.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    $profileActions = [ordered]@{}
    foreach ($identifier in @('SaveAudioProfileButton', 'ApplyAudioProfileButton')) {
        $profileAction = Get-ReleaseElement -Root $settingsWindow -Identifier $identifier
        $expectedEnabled = $profileUsbState.Confirmed -and ($identifier -eq 'SaveAudioProfileButton' -or $profileSelection.Count -eq 1)
        if ($profileAction.Current.IsEnabled -ne $expectedEnabled) {
            throw "Published profile guard disagrees with actual USB read state '$($profileUsbState.State)': $identifier"
        }
        $profileActions[$identifier] = $profileAction.Current.IsEnabled
    }
    if ((Get-ReleaseUsbReadState -Root $automation).Text -cne $profileUsbState.Text -or $cancelOperation.Current.IsEnabled) {
        throw 'USB state changed while checking profile guards; retry without forcing hardware changes.'
    }
    [void](Get-ReleaseElement -Root $settingsWindow -Identifier 'ResetAllSettingsButton')
    $settingsWindow.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    function Open-ReleaseSection {
        param([string]$MenuName, [string]$CommandId, [string]$WindowId, [string]$TitleKey)
        $menu = $automation.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $MenuName))
        $menu.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $command = $automation.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $CommandId))
        $command.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        $condition = [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $WindowId))
        $section = $null
        for ($attempt = 0; $attempt -lt 50 -and $null -eq $section; $attempt++) {
            Start-Sleep -Milliseconds 100
            $section = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $condition)
            if ($null -eq $section) { $section = $automation.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition) }
        }
        if ($null -eq $section) { throw "Published section was not found: $WindowId" }
        if ($section.Current.Name -cne (Get-ReleaseText $TitleKey)) { throw "Published section title is not localized: $WindowId" }
        return $section
    }
    $meter = Open-ReleaseSection -MenuName (Get-ReleaseText 'Main.Menu.ProgramName') -CommandId 'AudioMeterMenuItem' -WindowId 'AudioMeterWindow' -TitleKey 'Meter.Title'
    $startMeter = $meter.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'StartAudioMeter'))
    if ($null -eq $startMeter -or $startMeter.Current.IsEnabled) { throw 'Published meter unexpectedly selected an input or lacks its Start control.' }
    if ($startMeter.Current.Name -cne (Get-ReleaseText 'Meter.StartName')) { throw 'The audio meter Start caption is not localized.' }
    $resetPeaks = $meter.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'ResetAudioPeaks'))
    if ($null -eq $resetPeaks -or $resetPeaks.Current.IsEnabled -or $resetPeaks.Current.ControlType -ne [Windows.Automation.ControlType]::Button -or
        $resetPeaks.Current.Name -cne (Get-ReleaseText 'Meter.ResetPeaksName')) {
        throw 'Published peak reset must be a localized, disabled button before capture.'
    }
    $peakReadout = $meter.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'AudioLevelsReadout'))
    if ($null -eq $peakReadout) { throw 'Published static peak readout is missing.' }
    Assert-ReleaseStaticReadout -Element $peakReadout -ExpectedName (Get-ReleaseText 'Meter.Stopped')
    $meterStatus = $meter.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'SectionResultReadout'))
    $audioInputs = Get-ReleaseCaptionElement -Root $meter -Identifier 'AudioInputChoice' -Key 'Meter.InputName'
    $inputExpansion = $audioInputs.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    $inputExpansion.Expand()
    try {
        $inputCount = $audioInputs.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)).Count
    }
    finally { $inputExpansion.Collapse() }
    if ($audioInputs.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection().Count -ne 0 -or $startMeter.Current.IsEnabled) {
        throw 'Inspecting audio endpoints unexpectedly selected an input; capture was not requested.'
    }
    $meterMessage = if ($inputCount -eq 0) { Get-ReleaseText 'Meter.NoInputs' } else { Get-ReleaseText 'Meter.SelectInput' }
    $meterReadoutSuppressed = -not $showOperationMessages -and $inputCount -gt 0
    if ($meterReadoutSuppressed) {
        if ($null -ne $meterStatus -and -not $meterStatus.Current.IsOffscreen) { throw 'Optional meter guidance ignores disabled action messages.' }
    } else {
        if ($null -eq $meterStatus) { throw 'Required audio endpoint result is missing.' }
        Assert-ReleaseStaticReadout -Element $meterStatus -ExpectedName ((Get-ReleaseText 'Section.Result.WithMessage') -f $meterMessage)
    }
    $meter.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    $firmware = Open-ReleaseSection -MenuName (Get-ReleaseText 'Main.Menu.HelpName') -CommandId 'FirmwareMenuItem' -WindowId 'FirmwareWindow' -TitleKey 'Firmware.Title'
    $firmwareSafety = $firmware.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'FirmwareSafetyReadout'))
    if ($null -eq $firmwareSafety -or -not $firmwareSafety.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.IsReadOnly) {
        throw 'Published firmware guide lacks its read-only limitation notice.'
    }
    if ($firmwareSafety.Current.Name -cne (Get-ReleaseText 'Firmware.WarningName') -or
        $firmwareSafety.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -cne (Get-ReleaseText 'Firmware.Warning')) {
        throw 'The published firmware limitation notice is not localized.'
    }
    $firmware.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    $help = Open-ReleaseSection -MenuName (Get-ReleaseText 'Main.Menu.HelpName') -CommandId 'HelpMenuItem' -WindowId 'HelpWindow' -TitleKey 'Help.Title'
    [void](Get-ReleaseCaptionElement -Root $help -Identifier 'HelpReadout' -Key 'Help.GuideName')
    [void](Get-ReleaseCaptionElement -Root $help -Identifier 'LicensesReadout' -Key 'Help.LicensesName')
    $nestedTabs = $help.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Tab))
    if ($nestedTabs.Count -ne 0) { throw 'Published reference help unexpectedly adds nested tabs.' }
    $help.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    $information = Open-ReleaseSection -MenuName (Get-ReleaseText 'Main.Menu.HelpName') -CommandId 'InformationMenuItem' -WindowId 'InfoWindow' -TitleKey 'Info.Title'
    $diagnosticReadout = Get-ReleaseCaptionElement -Root $information -Identifier 'DiagnosticLogReadout' -Key 'Info.LogName'
    $diagnosticValue = $diagnosticReadout.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current
    $summaryReadout = Get-ReleaseCaptionElement -Root $information -Identifier 'DeviceInformationReadout' -Key 'Info.SummaryName'
    $summaryValue = $summaryReadout.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current
    if (-not $diagnosticValue.IsReadOnly -or -not $summaryValue.IsReadOnly) { throw 'Published diagnostics and device metadata must remain read-only.' }
    foreach ($refusal in @('Main.Change.TransmitterRequired', 'Target.SelectedUnavailable', 'Target.SharedParameters', 'Target.GroupUnavailable')) {
        if ($diagnosticValue.Value.Contains((Get-ReleaseText $refusal))) {
            throw 'Read-only startup/navigation unexpectedly requested a microphone setting edit.'
        }
    }
    $usbCommands = [regex]::Matches($diagnosticValue.Value, 'TX 0503AADD(?<command>[0-9A-F]{2})')
    foreach ($command in $usbCommands) {
        if ($command.Groups['command'].Value -cnotin @('1F', '0D')) {
            throw "Smoke diagnostics contain a USB command other than status/version reads: $($command.Groups['command'].Value)"
        }
    }
    $finalUsbState = Get-ReleaseUsbReadState -Root $automation
    if ($finalUsbState.Confirmed -and $usbCommands.Count -eq 0) { throw 'A confirmed USB read lacks its diagnostic read-command evidence.' }
    $information.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    $report = [ordered]@{
        Time = (Get-Date).ToString('o')
        Executable = $executable
        SHA256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
        PrismLibrarySHA256 = (Get-FileHash -LiteralPath $prismLibrary -Algorithm SHA256).Hash
        Bytes = $bytes.Length
        Version = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion
        NativeMachine = 'AMD64'
        NativeSubsystem = 'Windows GUI'
        FrameworkDependent = $true
        InterfaceLanguage = $InterfaceLanguage
        InitialInterfaceLanguage = $initialInterfaceLanguage
        ExternalLanguageCatalogs = $externalLanguageCatalogs
        CaptionSources = $captionSources
        FirstRunLanguagePreview = $needsSetup
        FirstRunThemeSelected = $setupCompleted
        LanguagePropertyPresent = $setupCompleted -or $languagePropertyPresent
        PersistedLanguage = if ($setupCompleted) { $saved.Language } elseif ($languagePropertyPresent) { $initialInterfaceLanguage } else { $null }
        EffectivePreferencesLanguage = if ($setupCompleted) { $saved.Language } else { $initialInterfaceLanguage }
        SettingsLanguageAndRestartNoticeChecked = $true
        FirstRunAction = if ($needsSetup) { $FirstRunAction } else { 'NotRequired' }
        FirstRunCompleted = $setupCompleted
        MainWindowCreated = $true
        SingleInstanceChecks = $singleInstanceChecks
        PlainSettingEditorNames = $editorNames
        NativeMicrophoneScopeItems = $microphoneScopeItems
        EscapeKeepsMainOpen = $true
        AudioMeterOpenedWithoutCapture = $true
        AudioPeakControlsCheckedWithoutCapture = $true
        AudioEndpointEnumeration = $meterMessage
        AudioEndpointCount = $inputCount
        OptionalMeterReadoutSuppressed = $meterReadoutSuppressed
        FirmwareGuideOpened = $true
        ReferenceMainReadoutsAndLists = $true
        InitialUsbReadState = $initialUsbState
        ProfileUsbReadState = $profileUsbState
        FinalUsbReadState = $finalUsbState
        ProfileActionsInspectedWithoutInvocation = $profileActions
        OfflineProfileActionsDisabled = if (-not $profileUsbState.Confirmed) { $true } else { $null }
        UsbReadOnlyDiagnosticsChecked = $true
        NoSpuriousStartupEditRefusals = $true
        UsbReadCommandCount = $usbCommands.Count
        UsbDiagnosticTrace = $diagnosticValue.Value
        DeviceInformation = $summaryValue.Value
        SinglePageHelpAndLicenses = $true
        Frameworks = $runtime.runtimeOptions.frameworks
        WindowName = $automation.Current.Name
        AutomationId = $automation.Current.AutomationId
        SettingsWrittenDuringSetup = $setupCompleted
        SettingsCategories = $categories
        GlobalShortcutControls = $shortcutSettings
        Limitations = 'Release host, selected-language captions, persisted language after restart, external language catalog hashes, setup-before-main order, one native USB selector and active readout, settings categories, profile gates against actual USB read state, read-only diagnostic command trace, native audio endpoint enumeration without capture, firmware guide, single-page help and normal exit checked with UI Automation. Automatic real USB status/version reads are allowed when a receiver is available; no receiver selection, manual connection, hardware-setting writes, identity-read invocation, registry writes or global input injection are performed. Actual NVDA listening, physical audio, deliberate hotplug, multi-receiver switching and keyboard/tray interaction remain manual.'
    }
    $report['ExitConfirmation'] = Close-ReleaseMain -OwnedProcess $process -WindowHandle $window -RequireConfirmation $confirmExit -RejectOnce
    if ($process.ExitCode -ne 0) { throw "Unexpected exit code: $($process.ExitCode)" }
    if ($setupCompleted -or -not $needsSetup) {
        $expectedSettingsHash = if ($setupCompleted) { $generatedSettingsHash } else { $settingsHash }
        if (-not (Test-Path -LiteralPath $settingsFile) -or (Get-FileHash -LiteralPath $settingsFile).Hash -ne $expectedSettingsHash) {
            throw 'Persisted configuration changed before restart; no second process was started.'
        }
        $restartPreferences = Get-Content -LiteralPath $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $restartLanguage = if ($null -ne $restartPreferences.PSObject.Properties['Language']) { $restartPreferences.Language } else { 'pl' }
        if (-not $restartPreferences.FirstRunCompleted -or $restartLanguage -cne $InterfaceLanguage) {
            throw 'Completed setup and the requested language were not persisted before restart.'
        }
        Assert-ReleaseCatalogsUnchanged
        $restart = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
        try {
            $restartWindow = [IntPtr]::Zero
            $restartSetupCondition = [Windows.Automation.AndCondition]::new(
                [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $restart.Id),
                [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'FirstRunWindow'))
            for ($attempt = 0; $attempt -lt 80 -and $restartWindow -eq [IntPtr]::Zero; $attempt++) {
                Start-Sleep -Milliseconds 100
                if ($restart.HasExited) { throw 'Completed configuration failed to start the main application.' }
                if ($null -ne [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $restartSetupCondition)) {
                    throw 'Completed setup incorrectly returned to the wizard after restart.'
                }
                $restartWindow = [HollylarkWindowProbe]::Find($restart.Id)
            }
            if ($restartWindow -eq [IntPtr]::Zero) { throw 'Completed setup did not open the main window on restart.' }
            $restartAutomation = [Windows.Automation.AutomationElement]::FromHandle($restartWindow)
            Assert-ReleaseMainLanguage -Root $restartAutomation
            $report['RestartExitConfirmation'] = Close-ReleaseMain -OwnedProcess $restart -WindowHandle $restartWindow -RequireConfirmation $confirmExit
            $report['CompletedSetupSkipsWizardAfterRestart'] = $true
            $report['RestartInterfaceLanguage'] = $InterfaceLanguage
            $report['EffectivePreferencesLanguageMatchesRestart'] = $true
            $report['PersistedLanguageMatchesRestart'] = if ($null -ne $restartPreferences.PSObject.Properties['Language']) { $true } else { $null }
        }
        finally {
            if (-not $restart.HasExited -and $restartWindow -ne [IntPtr]::Zero) {
                [void](Close-ReleaseMain -OwnedProcess $restart -WindowHandle $restartWindow -RequireConfirmation $confirmExit)
            }
            $restart.Dispose()
        }
        if ((Get-FileHash -LiteralPath $settingsFile).Hash -ne $expectedSettingsHash) { throw 'Restart modified the persisted configuration.' }
        Assert-ReleaseCommandErrorLanguage
        $report['CommandErrorRespectsSavedLanguage'] = $true
    }
    Restore-GeneratedSetup
    $currentSettingsHash = if (Test-Path -LiteralPath $settingsFile) { (Get-FileHash -LiteralPath $settingsFile).Hash } else { $null }
    if ($currentSettingsHash -ne $settingsHash) { throw 'Opening and closing settings unexpectedly changed the configuration file.' }
    Assert-ReleaseCatalogsUnchanged
    $report['LanguageCatalogsUnchanged'] = $true
    $report['NormalExitCode'] = $process.ExitCode
    $report['OriginalConfigurationRestored'] = $true
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    $report | ConvertTo-Json -Depth 8
}
catch {
    [ordered]@{
        Passed = $false; Executable = $executable; ProcessId = $process.Id
        Error = $_.ToString(); ScriptStackTrace = $_.ScriptStackTrace
        OriginalSettingsHash = $settingsHash
        CurrentSettingsHash = if (Test-Path -LiteralPath $settingsFile) { (Get-FileHash -LiteralPath $settingsFile).Hash } else { $null }
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    throw
}
finally {
    if (-not $process.HasExited) {
        $remainingMain = [HollylarkWindowProbe]::Find($process.Id)
        if ($remainingMain -ne [IntPtr]::Zero) {
            [void](Close-ReleaseMain -OwnedProcess $process -WindowHandle $remainingMain -RequireConfirmation $confirmExit)
        } else {
            $process.Refresh(); [void]$process.CloseMainWindow(); [void]$process.WaitForExit(5000)
        }
    }
    if ($process.HasExited) { Restore-GeneratedSetup }
    $process.Dispose()
}
