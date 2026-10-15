# Contributing to Hollylark

Thank you for helping improve an unofficial Windows controller for LARK A1. Start with the [English guide](README.md), [Polish guide](README.pl.md) and [known limits](README.md#known-limits). Hollylark is licensed under [GPL-3.0-only](LICENSE); dependencies retain their respective licenses.

## Reports and proposed changes

Use [GitHub issues](https://github.com/Pates2004/Hollylark/issues) for reproducible bugs and focused feature proposals. Include the application version, Windows/.NET version, interface language, relevant settings and exact steps, expected behavior and actual result. For receiver issues, include the receiver variant, firmware versions and connected transmitter count when known. Distinguish device readback from an audible or physical result.

For accessibility reports, include the NVDA version, keyboard sequence, focused control and expected/actual spoken text. Automated UI Automation output is useful evidence, but does not replace a listening test.

Review diagnostic attachments before posting. Remove personal paths, serial numbers, MAC addresses and other private identifiers. Do not submit credentials, user settings/profiles, raw recordings, vendor APKs, decompiled vendor code, firmware packages or artwork. Prefer a small synthetic regression fixture over a private device dump.

## Development and build

Use Windows x64, the .NET 10 SDK and PowerShell. Run commands from the checkout root:

```powershell
dotnet build .\LarkControl.slnx -c Release
dotnet run --project .\tests\Hollylark.Validation\Hollylark.Validation.csproj -c Release --no-build -- --core-only
dotnet run --project .\tests\Hollylark.Validation\Hollylark.Validation.csproj -c Release --no-build -- --single-instance-only
```

The validator is a console program; `dotnet test` does not run these checks. The core mode uses simulated device data. The single-instance mode uses isolated names and owned child processes; it does not launch the GUI or prove real window activation.

Optional WPF/UI Automation checks can run on a separate desktop after the solution build:

```powershell
$validationLog = 'Workspace\Validation\contribution-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.log'
.\tools\RunValidationDesktop.ps1 -Arguments @('--without-native-shell') -LogPath $validationLog
```

This skips native tray/menu and interactive window-restoration checks and uses simulated USB/audio. Some checks inspect the matching checkout's source files; do not run a copied validator against unrelated sources. Run `--single-instance-ui-only` separately on the ordinary interactive desktop to check main/setup/auxiliary window restoration with simulated devices and an in-memory tray. Report which checks you actually ran, their results and what remains untested. Do not infer hardware effects or NVDA speech quality from passing simulated checks.

To publish a local portable build:

```powershell
.\build.ps1
```

Current release files belong in local `installfiles`. Working artifacts use `D:\work\Workspace\<checkout-directory-name>` and previous builds/releases use `D:\work\Snapshots\<checkout-directory-name>`. The equivalent `C:\work` layout is used only if drive D: does not exist, never as a fallback for access or space errors. Use the existing path helpers rather than hardcoding a checkout name.

## Implementation expectations

- Keep changes focused. Write source identifiers and technical code in English; follow the existing C#/WPF structure and .NET 10 target.
- Preserve framework-dependent publication, `SelfContained=false`, `PublishSelfContained=false`, `UseAppHost=true` and the native Windows GUI host. Do not bundle .NET or introduce Native AOT.
- Keep application-owned settings, logs and other local data beside the actual executable. Preserve existing data and report write errors instead of silently using AppData.
- Keep PL/EN catalogs complete and consistent, including accessible names, errors and dynamic formatting. Update both READMEs when user-visible behavior changes, and add a concise changelog entry.
- Preserve native keyboard navigation, focus and accessible labels. New optional hints must have a saved on/off setting that also applies to screen-reader hints; essential labels, state and safety messages remain available.
- Add a meaningful regression case for a behavior fix. Test failure/cancellation paths and retained behavior, not just the successful path.
- Keep device state checks, acknowledgements and readback. Shared parameters must not be presented as independent per-microphone controls. Group operations must explain possible partial completion; cancellation must not imply rollback of commands already sent.
- Keep HidSharp/NAudio attribution and license resources intact; see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). Do not select or imply a project license on the maintainer's behalf.

## Manual and hardware validation

For UI changes, check keyboard focus, accessible names/states and actual NVDA speech in both languages. Check first-run setup, saved-language restart, minimized/tray windows and open auxiliary windows when changing single-instance activation. Test real window restoration separately from the isolated process checks. Note high-contrast/DPI checks and any limitations explicitly.

Keep hardware tests separate from routine validation. Only perform physical receiver reads, setting changes or audio capture with the device owner's explicit authorization. Setting tests can temporarily change gain/mute and other parameters; disconnection or interruption can prevent restoration. Do not run them during a recording, and do not add hardware modes to ordinary CI. There is no supported firmware writer or pairing workflow.

## Pull requests

Describe the concrete problem, resulting behavior and relevant validation. Include PL/EN and accessibility impact, any hardware interaction, and remaining risks or untested cases. Keep unrelated cleanup separate.

Commit source, project tests, build configuration and public documentation. Keep local instructions, the `example` reference, release ZIPs/executables, generated build files, assistant materials, diagnostic reports and user data out of the repository. Follow the existing exclusions rather than broadly ignoring directories that contain source or tests.
