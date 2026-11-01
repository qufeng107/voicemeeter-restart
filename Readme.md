# Voicemeeter Reconnect

A lightweight Windows desktop assistant that restores Voicemeeter hardware outputs after devices reconnect.

[中文说明](README-中文.md) · [Download v2.0.0](https://github.com/qufeng107/voicemeeter-restart/releases/tag/v2.0.0) · [Detailed configuration](desktop/README.md)

## Download and run

1. Download `VoicemeeterReconnect-2.0.0-win-x64.zip` from the release page.
2. Extract the whole folder to a permanent location.
3. Start Voicemeeter, then open `VoicemeeterReconnect.exe`.
4. Open **运行设置** to change recovery timing or enable startup at Windows sign-in.

Keep the background executable and both `.exe.config` files beside the main executable. Closing the configuration window leaves the tray monitor running. Use **退出程序** or the tray menu to stop monitoring and release the Remote API connection.

Requirements: Windows 10/11 x64, .NET Framework 4.8, and an installed copy of Voicemeeter. The official `VoicemeeterRemote64.dll` is discovered in the Voicemeeter installation directory; a custom path can be selected in settings. The release package does not redistribute that DLL.

## Features

- Native C# background monitor and an on-demand WPF configuration window.
- Automatically follows selected hardware outputs when devices or output buses change.
- Restarts only after an observed offline-to-online transition and a stable waiting period.
- Persistent offline devices do not cause repeated restarts.
- Configurable polling, settle delay, global cooldown, failure retry interval and retry limit.
- Current-user startup toggle, tray controls, single-instance protection and live configuration updates.
- Bounded event logs and clean Remote API logout.

The monitor preserves existing device bindings, routing, levels and effects. Disable Voicemeeter's **Auto Restart Audio Engine (All Device)** to avoid a second recovery mechanism repeatedly restarting disconnected outputs.

## Recovery settings

| Setting | Default |
|---|---:|
| Poll interval | 2 seconds |
| Reconnect settle delay | 7 seconds |
| Global restart cooldown | 15 seconds |
| Failure retry interval | 15 seconds |
| Additional retries | 0 |
| Start monitoring with the program | Enabled |
| Start at Windows sign-in | Disabled |

A retry limit of 2 allows at most 3 attempts, including the first attempt. Retries apply to failed restart commands. A successfully submitted command ends that recovery attempt; endpoint availability and API success do not verify audible playback.

Configuration and rotating logs are stored in `%LOCALAPPDATA%\VoicemeeterReconnect`. Longer delays may be needed for some Bluetooth devices. Basic, Banana and Potato output counts are supported; hardware testing was performed with Potato. ASIO or special drivers without matching Windows endpoint names may require further support.

## Build and test

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File desktop\build.ps1
```

The build uses the Windows .NET Framework compiler and writes the desktop package to `dist\VoicemeeterReconnect`.

```powershell
# Native logic checks; no audio changes
.\dist\VoicemeeterReconnect\VoicemeeterReconnect.exe --self-test --data-dir .\test-artifacts\native

# Legacy Python regression checks
py -m unittest -q test_monitor

# Real Windows integration checks; an existing monitor is required
powershell -NoProfile -ExecutionPolicy Bypass -File desktop\test.ps1
```

Adding `-RealRestart` to the integration test sends one real audio-engine restart command and briefly interrupts audio. Tests use a separate configuration and restore the original startup registration and production profile.

Validation: **33 native logic checks, 36 Windows integration checks and 9 Python regression checks passed**. The user subsequently confirmed the desktop version worked in device testing. See the [test report](TEST_REPORT-2026-11-01.md) for scope and limitations. One 30-second local sample measured about 44 MiB working set and 0.26% of one CPU core with the configuration window closed.

## Legacy Python helper

The original Python entry point and deterministic tests remain available:

```powershell
py -m pip install -r requirements.txt
py autorestart-audio-engine.py --self-test
py autorestart-audio-engine.py --diagnostic
```

Use one monitoring implementation at a time. The desktop version is the primary daily-use entry point.

## Credits and license

The project builds on [westarne/voicemeeter-auto-restart](https://github.com/westarne/voicemeeter-auto-restart) by Arne Westphal. The upstream MIT copyright and permission notice are retained in [LICENSE](LICENSE).
