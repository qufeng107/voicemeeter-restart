# Changelog

## v2.0.0 — 2026-11-01

First native Windows desktop release.

- Native C# monitor with an on-demand WPF settings window and tray controls.
- Automatically follows hardware output bindings across device and bus changes.
- Observes Windows endpoint transitions, waits for reconnect stability and respects a global restart cooldown.
- Configurable polling, settle delay, cooldown, failure retry interval and bounded additional retries.
- Current-user startup toggle, single-instance protection, live settings and bounded logs.
- Clean Remote API logout, invalid configuration/DLL recovery and Unicode device names.
- Preserves the legacy Python helper and regression checks.
- Fixes startup re-registration after moving the application and synchronizes pause/configuration updates with pending recovery work.

Validation: 33 native logic checks, 36 real Windows integration checks and 9 Python regression checks passed. Real native API invocation was checked; the user subsequently confirmed successful desktop device testing. Hardware validation was performed with Voicemeeter Potato.

Runtime: Windows 10/11 x64, .NET Framework 4.8 and an installed copy of Voicemeeter. The official Remote DLL is not redistributed.
