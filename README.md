# Hollylark

[Polski](README.pl.md) · [Changelog](CHANGELOG.md) · [Contributing](CONTRIBUTING.md)

An unofficial Windows application for controlling a **Hollyland LARK A1 USB-C** receiver. Hollylark provides native WPF controls, keyboard navigation and accessible labels for NVDA. It is independent of Hollyland and is not endorsed by the manufacturer.

**Source version 0.11.0** · Windows x64 · .NET Desktop Runtime 10 x64

## Download and launch

The latest published portable package is **v0.9.1**. This guide describes the **0.11.0 source**, including battery alerts; [build from source](#build) to use newer features before a matching package is released.

1. Download the portable ZIP from the [latest GitHub release](https://github.com/Pates2004/Hollylark/releases/latest).
2. Install **Microsoft .NET Desktop Runtime 10 for Windows x64** separately. Hollylark is framework-dependent: it does not bundle .NET. Its native Windows GUI host offers the standard .NET download prompt when the required runtime is missing.
3. Extract the entire ZIP into a writable folder. Keep these files together; do not run the EXE inside the ZIP:

   ```text
   Hollylark.exe
   prism.dll
   langs/
     pl.lng
     en.lng
   ```

4. Run `Hollylark.exe`, choose your first-run options and select **Continue**. **Cancel** exits without saving or opening the main window.

`prism.dll` is supplied with 0.10.1 builds; older published packages do not contain it.

Polish is the default language. You can select English in the first-run wizard. Later, choose **Program → Settings → Appearance → Language**, save with **OK**, then fully exit and restart the application. Both language files are required, regardless of the selected language.

Hollylark allows one running instance per Windows user/session. Launching it again restores the existing main window, first-run wizard or open auxiliary window, including when the application is minimized or hidden in the tray. A repeated launch does not restart the application or apply a saved language change.

## Features

- Automatic USB receiver discovery, status and firmware-version readouts, and connected transmitter battery levels.
- Configurable low/critical battery alerts with independent screen reader speech, Windows popup and system sound choices.
- Six gain levels, noise reduction, EQ, reverb, mono/stereo, LEDs and automatic power-off after 15 minutes without pairing.
- Individual indexed microphone mute/unmute and an **All microphones** target for group actions.
- Manual **Apply** by default; optional automatic application of new selections and local audio profiles.
- Optional Windows startup and tray integration, with explicit read, gain and mute actions.
- Light/dark appearance, adjustable text and readout presentation, and optional visual/screen-reader hints remembered between launches.
- Optional Windows input meters with RMS, sample peaks and retained clipping indications. Select an input and start measurement explicitly; Hollylark does not automatically capture audio, save recordings or send audio over the network.
- Local diagnostics, with serial/MAC identity reads available on request.
- Editable global shortcuts with keyboard capture, enable/disable and default restoration.
- Optional background screen-reader notifications for confirmed external device changes.
- Default-on preservation of volatile shared parameters after an observed microphone reconnection.

## Receivers and microphone controls

Connect the **USB-C receiver** using a data-capable cable or adapter and turn on the transmitters. The charging case is not a receiver. The sole detected receiver is selected and read automatically; if several are present, select one with the arrow keys in the receiver list. Startup discovery and an initial snapshot of already active microphones do not apply profiles or write settings. Enabled volatile-setting preservation can restore parameters after an observed reconnection, as described below.

Use the microphone list to choose an individual microphone or **All microphones**. The UI supports indices 1–4 when the receiver reports the corresponding topology; this does not convert two-transmitter firmware into four-transmitter firmware.

| Active microphones | Target for shared parameters | Mute/unmute |
| --- | --- | --- |
| Exactly one | Select that individual microphone | That microphone |
| Two or more | Select **All microphones** | An individual microphone or the group |

Gain, noise reduction, EQ, reverb and other device parameters are **shared receiver settings** in the known protocol. Selecting an individual microphone does not provide independent gain or EQ. With one active microphone, editing these parameters under its target still changes receiver-wide settings; their effect on transmitters that reconnect later is not established. Profiles follow the same target restrictions.

By default, choosing a value creates a draft; press **Apply** to send it. Automatic application is an optional setting, disabled by default. Changes require an acknowledgement and matching device readback. Changing the receiver, selected microphone/group or active microphone membership discards unsent drafts and queued automatic changes. **F5** also discards drafts before reading again.

Group mute/unmute sends commands sequentially to the active microphone indices, checks acknowledgements and requires a final readback confirming the whole group. A mixed group is muted; a fully muted group is unmuted. This is not an atomic operation: a failure, disconnection or cancellation can leave some microphones changed. A partial-operation warning means you should read the current state again; it does not mean earlier writes were rolled back.

## Battery alerts

Open **Settings → Battery alerts**. The master **Enable battery alerts** switch is enabled by default. Set whole percentage thresholds with **1 ≤ critical < low ≤ 100**; the default thresholds are inclusive: low at **20% or below**, critical at **10% or below**. Invalid thresholds prevent saving. **OK** saves the draft; leaving with unsaved changes lets you save or discard it. Resetting this category restores its defaults.

Each level has its own output choices:

| Setting | Low battery | Critical battery |
| --- | --- | --- |
| Threshold | 20% | 10% |
| Windows popup notification | Off | Off |
| Screen reader speech through Prism | On | On |
| System sound | Off | Off |

Every detected warning appears in the main status and diagnostic log, regardless of the output choices. This status update does not itself request screen reader speech. The notification checkbox controls only the optional native Windows tray balloon; Windows notification suppression still applies. The speech checkbox controls Hollylark's own requests through the existing Prism background pipeline, independently of **Announce device state changes through the screen reader**. It works while another application is active or Hollylark is minimized or hidden, without moving focus. NVDA may independently read Windows notifications according to its own and Windows' settings: disabling Hollylark's speech option does not prevent that. Enabling both popup and speech can therefore produce duplicate spoken warnings.

Warnings detected in the same status read are combined into at most **one Windows popup and one speech request**, critical first. Each level contributes only to its enabled output channels. At most **one system sound** plays, using the highest severity whose sound is enabled: Windows **Exclamation** for low or **Hand** for critical. The Windows sound scheme applies and may be silent. The balloon itself does not request an additional sound.

Detection uses the **reported percentage** of each connected microphone 1–4, including a reported 0%; 0% does not establish that the battery is truly empty. Unknown readings and disconnected microphones are ignored. An initially low known reading warns immediately; an initially critical reading produces only the critical warning. Each microphone warns once per threshold until a reading reaches **at least five percentage points above that threshold, capped at 100%**. With the defaults, critical rearms at 15% and low at 25%. Memory is separate per receiver USB path and microphone; brief reconnections and receiver switches preserve it during the current run. Warning history is not stored on disk. Changing thresholds or the master switch resets detection; changing only output choices does not replay warnings.

Popup notifications reuse an existing tray icon. An icon created for them remains until exit or until the master switch or both levels' Windows notifications are disabled. It remains if closing to the tray is enabled; no second icon is created.

Alerts perform no microphone actions: they do not mute or power off transmitters. **Immediate remote power-off has not been verified.** The existing automatic power-off after 15 minutes without pairing is a separate device policy, unrelated to battery alerts. Case battery and charging telemetry are unavailable. Neither documentation nor automated checks certify speech or sound heard by a person, actual battery capacity or physical device effects.

## Global shortcuts and automatic restoration

Open **Settings → Keyboard shortcuts**, select an action with the arrow keys, then Tab to its editable shortcut field and **Capture shortcut** button. Capture listens only after pressing that button; Escape cancels capture without closing Settings. Clear the field to unassign an action. Invalid, duplicate or occupied combinations are reported before saving.

Global shortcuts are **disabled by default**. Enabling/disabling keeps the assignments; resetting this category restores its defaults and disables it again. Defaults use Ctrl+Alt+Shift: F1 shows Hollylark, F5 reads status, F2 toggles the selected microphone/group, F3 toggles all microphones, Up/Down change gain, F6 cycles noise reduction, F7 cycles EQ, F8 cycles reverb, F9 toggles LEDs and F10 changes mono/stereo. Auto-off and identification actions start unassigned. Defaults avoid letter keys so they do not consume AltGr text entry. Hardware actions use fresh status and the existing individual/group restrictions without moving focus. A global read refuses when unsent drafts exist; ordinary F5 still explicitly discards drafts. Registrations are suspended while Settings is open and released on exit. Hollylark must remain running for global shortcuts to work.

The existing **Announce device state changes through the screen reader** option sends confirmed changes made through a phone or microphone buttons directly to a running reader through **Prism**, using its .NET binding **Prismatoid**. This does not require an active Hollylark window: it also works while minimized or hidden in the tray. Keep `prism.dll` beside the EXE. NVDA has priority if several supported readers are running; standalone SAPI/OneCore voices are not selected. Direct requests do not interrupt current reader speech, move focus, repeat unchanged values or announce battery fluctuations. A successful direct request suppresses the duplicate UI Automation notification. If no supported reader is available or direct output fails, Hollylark falls back to UI Automation; presentation then depends on reader policy. Readers must share the Windows session and desktop. Automated tests do not certify speech actually heard by a person.

**Preserve volatile microphone settings** is enabled by default. Confirmed gain, channels, noise reduction, EQ, reverb, LEDs, auto-off and supported identification settings are remembered per receiver USB path in `data/retained-settings.json`, beside the EXE. Continuous-session external changes become the new baseline. After a detected disconnection/reconnection, differing shared parameters are restored with fresh group checks, conflict detection and readback. This affects the whole active group, regardless of the displayed individual target. Mute is never restored and microphones are never automatically unmuted. If the first device snapshot already has active microphones, their actual state is adopted without writing an old configuration; if startup first observes them disconnected, remembered settings can be restored when they return. A different USB path gets its own baseline. Power cycles entirely between polls or while Hollylark is closed cannot be identified reliably; the protocol does not report why a value changed. Failures or concurrent changes stop the attempt without repeated write loops. Unavailable noise/identification changes are not forced by unmuting or bypassing microphone-count gates. Unsent drafts or open Settings prevent a new restoration attempt. F5 and explicit identity reads remain read-only; a deferred reconnect restoration can occur on a later background poll. Disable preservation to prevent these automatic writes.

## Keyboard and NVDA

| Key | Action |
| --- | --- |
| Tab / Shift+Tab | Move between controls |
| Arrow keys | Navigate native lists, including receivers and microphone targets |
| Ctrl+Tab / Ctrl+Shift+Tab | Change tabs |
| Enter / Space | Activate a button |
| Alt | Open the menu |
| F5 | Read or retry the receiver; discard pending device choices |
| Alt+F4 | Close, or hide to tray according to settings |
| Win+B | Reach the Windows notification area when tray integration is enabled |

Escape dismisses open menus/dropdowns, keeps the main window and first-run wizard open, and closes ordinary auxiliary windows. Leaving Settings with unsaved edits asks whether to save: **Yes** saves, **No** discards. Save errors keep the settings window open.

Readouts have accessible names and focusable presentation. Optional hints, including screen-reader hints, can be disabled and re-enabled in Settings without removing essential names, errors or safety warnings. Automated UI Automation checks cannot establish actual NVDA speech quality; real-reader, keyboard, tray, high-contrast and DPI checks remain part of manual validation.

## Local data and updates

Preferences and audio profiles are stored in `data/settings.json`; default diagnostic reports go to `reports`, both beside the **actual EXE**, independently of the terminal working directory. Hollylark does not silently fall back to AppData. Use a writable portable folder. Invalid or unsupported settings produce an error instead of being silently replaced. No telemetry is sent.

Settings schema 5 adds battery alert preferences. Older supported settings migrate with the defaults above while preserving existing choices; reading them does not rewrite the file. An explicit save persists the migrated settings. Battery warning episodes remain in memory only.

Before updating, fully exit Hollylark. Preserve your `data` folder and any reports you need, and replace the application files and both catalogs together from the new ZIP. If Windows startup is enabled, disable it before moving the portable folder and enable it again from the new location.

Diagnostics may contain USB device paths, firmware versions, serial numbers, MAC addresses and raw reports. Review them before sharing publicly.

## Known limits

- Parity with the latest global Google Play version of HollyAudio is **not certified**.
- Firmware installation, update-availability checks, transmitter pairing and charging-case battery/status are not implemented. The firmware page provides version information and guidance, not an updater.
- There is no independent per-microphone gain/EQ, Bluetooth or Lightning-receiver support, or ordinary setting to switch between two- and four-transmitter firmware.
- Mute requires a connected transmitter and confirmed receiver firmware of at least 1.0.1.3. Other controls are gated by reported device state and capabilities.
- Windows audio meters measure the explicitly selected Windows input, not per-transmitter levels over the USB control protocol or calibrated acoustic loudness.
- A matching readback does not prove an audible effect, physical LED output, elapsed power-off behavior or complete hardware compatibility. A successful build is not an accessibility or hardware certification.

## Build

Use Windows x64, the **.NET 10 SDK** and PowerShell. From the checkout root:

```powershell
.\build.ps1
```

The script publishes the current framework-dependent x64 release into local `installfiles`, keeping `Hollylark.exe` with its `langs` folder and preserving existing `data` and `reports`. It retains the native GUI host and does not use self-contained publication or Native AOT.

Build artifacts go to `D:\work\Workspace\<checkout-directory-name>`. Superseded builds/releases go to `D:\work\Trash\<checkout-directory-name>` without deletion; retain a previous build in `Snapshots` only for a documented current diagnostic or recovery dependency. Only when drive D: does not exist is the equivalent `C:\work` layout used. An unwritable or full D: does not trigger a switch to C:.

## Validation

Version 0.11.0 passed **42,790 checks** in the full isolated Windows regression suite. Battery coverage includes threshold recovery, independent output choices, settings migration and failure preservation, localized native controls, and active/inactive/minimized/hidden delivery without focus movement. It also checks that read, profile and shortcut completion neither erase a new warning nor bypass its speech preference. Separate native checks loaded the pinned x64 Prism library and initialized the running NVDA backend without speech or cancellation (8), restored owned WPF windows (12), exercised the Explorer tray (9) and verified artifact safety (9). These sets overlap. Actual speech and sound heard by a person, physical keyboard-triggered cross-application dispatch and microphone effects still require user/hardware testing.

Run the console checks with:

```powershell
dotnet build .\LarkControl.slnx -c Release
dotnet run --project .\tests\Hollylark.Validation\Hollylark.Validation.csproj -c Release --no-build -- --core-only
dotnet run --project .\tests\Hollylark.Validation\Hollylark.Validation.csproj -c Release --no-build -- --single-instance-only
```

The validator is a console executable, not a `dotnet test` suite. Core checks use simulated device data; single-instance checks use isolated names and owned child processes without a GUI or hardware. They do not verify restoration of real windows or NVDA speech.

For optional WPF/UI Automation validation on a separate desktop, run after building the solution:

```powershell
$validationLog = 'Workspace\Validation\public-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.log'
.\tools\RunValidationDesktop.ps1 -Arguments @('--without-native-shell') -LogPath $validationLog
```

The relative log path resolves into the checkout's central Workspace. This mode skips native tray/menu and interactive window-restoration checks and uses simulated USB/audio. Run from the matching source checkout; some checks inspect source files. Hardware modes are not part of these commands.

Run window-restoration checks on the ordinary interactive desktop:

```powershell
dotnet run --project .\tests\Hollylark.Validation\Hollylark.Validation.csproj -c Release --no-build -- --single-instance-ui-only
```

These checks use WPF windows, a simulated receiver and an in-memory tray. Actual NVDA speech, physical audio and native tray interaction remain separate checks.

## Third-party notices

Hollylark uses HidSharp, NAudio and Prism through Prismatoid. Keep their attribution and license texts intact; see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) and the library licenses available in the application's Help window.

## License

Hollylark is licensed under the **GNU General Public License v3.0 only (GPL-3.0-only)**. See [LICENSE](LICENSE) for the complete text. Dependencies retain their respective licenses. Source code matching each portable release is available under the corresponding Git tag.
