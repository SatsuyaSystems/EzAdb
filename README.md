# EzAdb

EzAdb is a Windows desktop app for managing Android devices over USB or Wi-Fi with ADB and scrcpy. Install APKs, control and record screens, browse files, and run macros from one place.

## Getting started

1. Download `EzAdb-win-x64.zip` from the [latest release](https://github.com/SatsuyaSystems/EzAdb/releases/latest).
2. Extract the entire ZIP and open `EzAdb.exe`. The .NET runtime is included. Choose **Set up automatically** to download ADB and scrcpy if needed.
3. Enable USB debugging in Android developer options, connect a USB cable, and approve the prompt on Android. For Wi-Fi, open **Wireless debugging** on Android and pair with a QR code or six-digit pairing code in EzAdb. If a paired device does not appear, enter the connection address shown on Android. Saved Wi-Fi devices can be disconnected and reconnected from the Wi-Fi page. [ADB documentation](https://developer.android.com/tools/adb#wireless-android11-command-line)

## What you can do

- **Screen control** — Use your mouse and keyboard, save screenshots, and choose whether audio plays on Android or your PC. **Keep audio on device** is on by default. Changing it reopens active screen windows. [scrcpy audio documentation](https://github.com/Genymobile/scrcpy/blob/v5.0.1/doc/audio.md)
- **Files** — Browse Android folders with Quick access, breadcrumbs, back and forward navigation, and search. Use the context menu to rename, delete, or save items. Drag files from Windows onto the file list or a folder. To drag files the other way, select them and choose **Prepare for drag and drop**.
- **Windows File Explorer** — Open **Files → File Explorer**. For USB access, enable **File transfer** on Android and find the phone under **This PC**. Wireless File Explorer access uses Link to Windows and requires Windows 11 and Android 11 or later. [Microsoft walkthrough](https://support.microsoft.com/en-us/windows/experience/fileexplorer/setting-up-and-using-your-phone-in-file-explorer)
- **Apps and APKs** — Install APKs, update existing apps, and manage installed apps. You can launch, stop, enable, disable, or uninstall apps, view details, and export base and split APKs.
- **Studio** — Record MP4 video with configurable bitrate, FPS, and resolution. Device audio requires Android 11 or later. Keeping playback on Android while recording audio requires Android 13 or later. Camera preview and recording require Android 12 or later. Saving camera photos uses FFmpeg from the tools folder or Windows PATH. [Recording](https://github.com/Genymobile/scrcpy/blob/v5.0.1/doc/recording.md) · [Camera](https://github.com/Genymobile/scrcpy/blob/v5.0.1/doc/camera.md)
- **Virtual displays** — On Android 10 or later, use **Screen → Virtual displays → Choose app** to select an app by name and open it in a separate scrcpy window. Use the active Studio session to open another app on that display. [Virtual display documentation](https://github.com/Genymobile/scrcpy/blob/v5.0.1/doc/virtual-display.md)
- **Experimental app audio** — The Windows release includes an EzAdb audio server that routes only the selected virtual display app to the PC. Enable **Play only the selected app’s audio on this PC** before starting the display. Requires Android 13 or later and scrcpy 5.0.1, which **Set up automatically** can install. The route is tied to the app selected at launch. Start a separate display to choose a different app.
- **Macros and auto-clicker** — Open **Screen → Open macro controls** for an active session. Choose **Set click points**, click the preview to place numbered points, and set each point’s order, delay, hold time, and repeat count. Save the sequence, then use **Run** and **Stop**. Use the default scrcpy orientation while placing points and keep the Android orientation fixed while running. Configure autorun on device connection in **Screen → Macro library**.
- **Remote control and ADB console** — Send Android keys and text, run ADB commands, view streaming output, and recall commands from the current session.
- **Device monitor and batch actions** — View live battery, temperature, CPU, RAM, and storage metrics. Install APKs, transfer files, run ADB commands, or open screen windows for multiple devices.
- **Backups** — Back up accessible Android files to a Windows folder and restore individual files or entire sources.

Android version, device manufacturer, and work profile restrictions affect which features are available. ADB can access only the data Android exposes to it.

## Build and test

Use the .NET 8 SDK on Windows.

```powershell
dotnet run
dotnet publish -c Release -r win-x64 --self-contained true -p:DebugType=none -p:DebugSymbols=false -o artifacts/EzAdb-win-x64
Start-Process .\artifacts\EzAdb-win-x64\EzAdb.exe -ArgumentList '--self-test' -Wait -PassThru
```

Exit code `0` indicates a passing self-test. EzAdb looks for ADB and scrcpy on PATH or in the `tools` folder next to the executable. Automatic setup verifies the official scrcpy archive against a pinned SHA-256 checksum. **Manage tools** shows where the executables are installed. [Official scrcpy packages](https://github.com/Genymobile/scrcpy/releases/tag/v5.0.1)

EzAdb saves its settings, Wi-Fi device profiles, and macros locally in your Windows user profile. Automatic setup fetches the official scrcpy archive from GitHub.

To build the optional app audio server, run `AppAudio/Build-Server.ps1` with a clean scrcpy 5.0.1 source tree, Android SDK Platform 36, Android Build Tools 36.0.0, and a JDK. Set `-Output` to `tools/ezadb-app-audio-server` next to the EzAdb executable. EzAdb checks the server SHA-256 and the desktop scrcpy version before enabling audio capture.
