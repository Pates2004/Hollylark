# Hollylark

[Polski](README.pl.md) · [Changelog](CHANGELOG.md) · [Contributing](CONTRIBUTING.md)

An unofficial Windows application for controlling a **Hollyland LARK A1 USB-C** receiver. Hollylark provides native WPF controls, keyboard navigation and accessible labels for NVDA. It is independent of Hollyland and is not endorsed by the manufacturer.

**Version 0.9.1** · Windows x64 · .NET Desktop Runtime 10 x64

## Download and launch

1. Download the portable ZIP from the [latest GitHub release](https://github.com/Pates2004/Hollylark/releases/latest). The first public release is **v0.9.1**.
2. Install **Microsoft .NET Desktop Runtime 10 for Windows x64** separately. Hollylark is framework-dependent: it does not bundle .NET. Its native Windows GUI host offers the standard .NET download prompt when the required runtime is missing.
3. Extract the entire ZIP into a writable folder. Keep these files together; do not run the EXE inside the ZIP:

   ```text
   Hollylark.exe
   langs/
     pl.lng
     en.lng
   ```

4. Run `Hollylark.exe`, choose your first-run options and select **Continue**. **Cancel** exits without saving or opening the main window.

Polish is the default language. You can select English in the first-run wizard. Later, choose **Program → Settings → Appearance → Language**, save with **OK**, then fully exit and restart the application. Both language files are required, regardless of the selected language.

Hollylark allows one running instance per Windows user/session. Launching it again restores the existing main window, first-run wizard or open auxiliary window, including when the application is minimized or hidden in the tray. A repeated launch does not restart the application or apply a saved language change.

## Features

- Automatic USB receiver discovery, status and firmware-version readouts, and connected transmitter battery levels.
- Six gain levels, noise reduction, EQ, reverb, mono/stereo, LEDs and automatic power-off after 15 minutes without pairing.
- Individual indexed microphone mute/unmute and an **All microphones** target for group actions.
- Manual **Apply** by default; optional automatic application of new selections and local audio profiles.
- Optional Windows startup and tray integration, with explicit read, gain and mute actions.
- Light/dark appearance, adjustable text and readout presentation, and optional visual/screen-reader hints remembered between launches.
- Optional Windows input meters with RMS, sample peaks and retained clipping indications. Select an input and start measurement explicitly; Hollylark does not automatically capture audio, save recordings or send audio over the network.
- Local diagnostics, with serial/MAC identity reads available on request.

## Receivers and microphone controls

Connect the **USB-C receiver** using a data-capable cable or adapter and turn on the transmitters. The charging case is not a receiver. The sole detected receiver is selected and read automatically; if several are present, select one with the arrow keys in the receiver list. Discovery and automatic status reads do not apply profiles or write audio settings.

Use the microphone list to choose an individual microphone or **All microphones**. The UI supports indices 1–4 when the receiver reports the corresponding topology; this does not convert two-transmitter firmware into four-transmitter firmware.

| Active microphones | Target for shared parameters | Mute/unmute |
| --- | --- | --- |
| Exactly one | Select that individual microphone | That microphone |
| Two or more | Select **All microphones** | An individual microphone or the group |

Gain, noise reduction, EQ, reverb and other device parameters are **shared receiver settings** in the known protocol. Selecting an individual microphone does not provide independent gain or EQ. With one active microphone, editing these parameters under its target still changes receiver-wide settings; their effect on transmitters that reconnect later is not established. Profiles follow the same target restrictions.

By default, choosing a value creates a draft; press **Apply** to send it. Automatic application is an optional setting, disabled by default. Changes require an acknowledgement and matching device readback. Changing the receiver, selected microphone/group or active microphone membership discards unsent drafts and queued automatic changes. **F5** also discards drafts before reading again.

Group mute/unmute sends commands sequentially to the active microphone indices, checks acknowledgements and requires a final readback confirming the whole group. A mixed group is muted; a fully muted group is unmuted. This is not an atomic operation: a failure, disconnection or cancellation can leave some microphones changed. A partial-operation warning means you should read the current state again; it does not mean earlier writes were rolled back.

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

Build artifacts go to `D:\work\Workspace\<checkout-directory-name>`; previous builds/releases are moved into `D:\work\Snapshots\<checkout-directory-name>`. Only when drive D: does not exist is the equivalent `C:\work` layout used. An unwritable or full D: does not trigger a switch to C:.

## Validation

Version 0.9.1 passed 34,663 checks in the full isolated Windows regression suite. Separate runs passed 24 process-coordination checks and 12 WPF window-restoration checks. These sets overlap. Actual NVDA speech and physical microphone effects require hardware/user testing.

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

Hollylark uses HidSharp and NAudio. Keep their attribution and license texts intact; see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) and the library licenses available in the application's Help window.

## License

Hollylark is licensed under the **GNU General Public License v3.0 only (GPL-3.0-only)**. See [LICENSE](LICENSE) for the complete text. Dependencies retain their respective licenses. Source code matching each portable release is available under the corresponding Git tag.
