# Changelog

User-visible changes are listed here. Entries describe changes, not a certification of hardware compatibility, NVDA speech or completed release validation.

## [0.11.0] - 2026-10-28

### Added

- Native **Battery alerts** settings with a master switch enabled by default, inclusive low/critical thresholds (20%/10% by default), and independent output choices per level. Whole thresholds must satisfy `1 ≤ critical < low ≤ 100`. Reset, save/cancel and settings schema 5 migration preserve the existing preferences workflow.
- Default battery warnings through the existing Prism background speech route, independent of general device-state announcements and without requiring an active window. Optional Windows tray balloons and system sounds are off by default. Warnings always appear in the main status and log; the status update itself does not request speech.
- Same-read warnings combined into one popup and one speech request at most, critical first, with each severity's output choices respected. One optional sound uses the highest severity with sound enabled: Windows Exclamation for low or Hand for critical, following the system sound scheme. Windows notification suppression applies; NVDA may independently read optional Windows popups, so disabling Hollylark speech does not control OS/reader policy or prevent duplicate speech when both are enabled.
- Separate warning episodes for each receiver USB path and microphone 1–4. Initial known low readings warn; an initial critical reading emits only critical. Repeated warnings wait for recovery at least five percentage points above each threshold, capped at 100%. Unknown/disconnected readings are ignored; a connected reported 0% is accepted without certifying a truly empty battery. Brief reconnections and receiver switches retain memory during the run; no warning history is saved to disk. Threshold/master changes reset detection, while output-only changes do not replay warnings.
- Notification tray icon reuse and lifetime until exit or disabling the master switch or both visual channels; closing to the tray can retain the icon. Complete Polish/English labels, messages, help and guides.

### Limitations

- Warnings perform no microphone actions. Immediate remote power-off has not been verified; the existing 15-minute unpaired auto-off policy is unrelated. No charging-case battery or charging telemetry is added.
- Audible speech, sound and physical device behavior are not certified by these changes. The latest published GitHub portable package remains **v0.9.1**; this entry describes source version 0.11.0, not a new binary release.

## [0.10.1] - 2026-10-27

### Fixed

- Send background device announcements directly to a running screen reader through Prismatoid 0.4.0 / Prism v0.17.3, without requiring an active Hollylark window or requesting speech cancellation.
- Preserve the existing announcement preference and UI Automation fallback; do not also send a duplicate notification after a successful direct request or select standalone TTS voices.
- Own native backend handles explicitly so unavailable/restarted readers and initialization failures do not leak handles through the binding's high-level acquisition path.
- Ship the pinned Windows x64 prism.dll beside the executable and include upstream notices and license texts in Help.

## [0.10.0] - 2026-10-20

### Added

- Editable opt-in global shortcuts, native action list, keyboard capture, conflict reporting, enable/disable and category defaults.
- Default-on retention of confirmed volatile shared parameters after observed microphone reconnections, isolated by receiver USB path and persisted beside the executable. Never restores mute or automatically unmutes microphones.
- Fresh expected-state guards, bounded restoration, strict local retention files and focused simulated-device/native API regression tests.

### Fixed

- Existing device-state announcements now use a background UI Automation notification provider without moving focus; unchanged polls remain silent.
- Shortcut registration/persistence transactions keep unsaved bindings inactive and recover from conflicts without nested settings suspension.
- Explicit status and identity reads remain read-only while preserving deferred reconnect restoration for subsequent background polling.
- Build publication follows the current category policy: ordinary superseded binaries go to Trash, not indefinite archival snapshots; user data remains beside the current EXE.

## [0.9.1] - 2026-10-15

First public release in `Pates2004/Hollylark`, with a portable Windows x64 ZIP under tag `v0.9.1`.

### Added

- Single-instance coordination per Windows user/session. Repeated launches request restoration of the existing main window, first-run wizard or open auxiliary window, including minimized/tray cases.
- Isolated process checks for single-instance ownership, activation signalling and recovery, available through `--single-instance-only`.
- English main README, a matching Polish guide and English contribution instructions for the public repository.
- GPL-3.0-only project license and Windows/.NET 10 build validation on GitHub Actions.

### Changed

- Polish settings label now uses **Pokaż diody LED** instead of **Pokaż lampki**.
- Current portable files remain in local `installfiles`; build artifacts and previous builds use the central Workspace and Snapshots categories, named after the checkout directory.

### Compatibility

- Continues to target Windows x64 and .NET Desktop Runtime 10 with framework-dependent publication and a native Windows GUI host.
- Keeps shared receiver parameters and indexed individual mute semantics; no independent per-microphone gain/EQ or firmware updater is introduced.

## [0.9.0]

- Added individual microphone and **All microphones** targets, including indices up to four when supported by reported device topology.
- Allowed shared parameters under the sole active microphone; required **All microphones** when at least two are active. Individual mute/unmute remains indexed.
- Added sequential group mute/unmute with acknowledgements, final group readback and warnings about partial completion.
- Discarded unsent drafts and queued automatic changes when the selected target or active microphone membership changes; profiles follow the same scope restrictions.

Earlier private development history is not reproduced here. See the [current guide](README.md) for capabilities and limitations.
