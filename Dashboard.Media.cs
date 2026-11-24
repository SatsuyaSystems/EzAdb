using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using static EzAdb.DeviceCommands;

namespace EzAdb;

internal sealed partial class Dashboard
{
    private sealed class StudioSession(Device device, string kind, CancellationTokenSource cancellation)
    {
        public Device Device { get; } = device;
        public string Kind { get; } = kind;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public int? DisplayId { get; set; }
        public MacroSession? Macros { get; set; }
        public Task Completion { get; set; } = Task.CompletedTask;
        public override string ToString() => $"{Kind}  ·  {Device.DisplayName}" + (DisplayId is { } id ? $"  ·  Display {id}" : "");
    }

    private readonly ObservableCollection<StudioSession> studioSessions = [];
    private bool closingStudio;
    private Action<int>? showStudioTab;

    private DevicePage BuildMediaPage()
    {
        var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var recording = BuildRecordingPanel();
        var camera = BuildCameraPanel();
        var display = BuildDisplayPanel(out var deviceChanged);
        var sessions = UiList("Active Studio sessions", 116);
        sessions.ItemsSource = studioSessions;
        var displayActions = UiRow(
            UiAction("Choose an app for this display", async () =>
            {
                var session = sessions.SelectedItem as StudioSession ??
                    (studioSessions.Where(item => item.DisplayId is not null).ToArray() is [var displaySession] ? displaySession : null);
                if (session is not { DisplayId: { } id })
                    throw new InvalidOperationException("Choose an active virtual display.");
                if (PickAndroidApp(session.Device, this) is { } app && studioSessions.Contains(session))
                {
                    await AndroidShell(session.Device, Launch(app.Package, id));
                    Notify($"Opened {app.Name} on display {id}.");
                }
            }), UiAction("Open macro controls", () =>
            {
                if (sessions.SelectedItem is not StudioSession { Macros: { } macros })
                    throw new InvalidOperationException("Choose a running virtual display.");
                ShowMacroControls(macros);
                return Task.CompletedTask;
            }));
        var tabs = UiRow();
        (string Label, FrameworkElement View)[] pages = [("Record screen", recording), ("Phone camera", camera), ("Virtual displays", display)];
        void Select(int index)
        {
            var (label, view) = pages[index];
            host.Content = view;
            displayActions.Visibility = Visible(ReferenceEquals(view, display));
            foreach (var button in tabs.Children.Cast<Button>())
                button.Style = (Style)FindResource(Equals(button.Content, label) ? "Primary" : "Action");
        }
        for (var index = 0; index < pages.Length; index++)
        {
            var tab = index;
            tabs.Children.Add(UiAction(pages[tab].Label, () => { Select(tab); return Task.CompletedTask; }));
        }
        showStudioTab = Select;
        Select(0);
        var tracker = UiCard(UiText("Active Studio sessions", true), sessions,
            UiRow(UiAction("Stop session", async () =>
            {
                var session = sessions.SelectedItem as StudioSession ?? (studioSessions.Count == 1 ? studioSessions[0] : null);
                if (session is null) throw new InvalidOperationException("Choose a session.");
                session.Cancellation.Cancel();
                await session.Completion;
            })), displayActions);
        tracker.Visibility = Visible(studioSessions.Count > 0);
        studioSessions.CollectionChanged += (_, change) =>
        {
            tracker.Visibility = Visible(studioSessions.Count > 0);
            if (change.NewItems is { Count: > 0 } added) sessions.SelectedItem = added[0];
        };
        return new DevicePage(UiStack(tabs, host, tracker), "Studio", "Record the screen or camera and open separate Android displays.", deviceChanged);
    }

    private void OpenStudioTab(int index)
    {
        MediaNav.IsChecked = true;
        showStudioTab?.Invoke(index);
        PageScroll.ScrollToTop();
    }

    private void OnOpenRecording(object sender, RoutedEventArgs e) => OpenStudioTab(0);
    private void OnOpenCamera(object sender, RoutedEventArgs e) => OpenStudioTab(1);
    private void OnOpenVirtualDisplays(object sender, RoutedEventArgs e) => OpenStudioTab(2);
    private void OnOpenMacroLibrary(object sender, RoutedEventArgs e) => MacrosNav.IsChecked = true;

    private FrameworkElement BuildRecordingPanel()
    {
        var size = UiField("Maximum side length", "1920", 140);
        var bitRate = UiField("Video bitrate in Mbit/s", "8", 140);
        var fps = UiField("Maximum FPS", "30", 110);
        var audio = UiCheck("Record device audio");
        return UiCard(UiText("Record the screen as MP4", true),
            UiRow(Labeled("Max. side length (px)", size), Labeled("Quality (Mbit/s)", bitRate), Labeled("FPS limit", fps)),
            UiRow(audio), UiText("Recording preserves the aspect ratio. Device audio requires Android 11. Keeping playback on the device while recording requires Android 13."),
            UiAction("Start recording", async () =>
            {
                var device = RequireDevice();
                var edge = ReadNumber(size, "Side length", 160, 8192);
                var frames = ReadNumber(fps, "FPS", 1, 240);
                var mbps = ReadNumber(bitRate, "Quality", 1, 200);
                var sound = audio.IsChecked == true;
                if (sound) await RequireAndroid(device, keepAudioOnDevice ? 33 : 30,
                    keepAudioOnDevice ? "Audio recording with playback on the device" : "Audio recording");
                if (ChooseMediaFile("Save screen recording", "MP4 video (*.mp4)|*.mp4", "Display", "mp4") is not { } destination) return;
                BeginStudio(device, "Screen recording", RecordingOptions(edge, frames, mbps, sound, keepAudioOnDevice), destination);
            }, true));
    }

    internal static string[] RecordingOptions(int edge, int fps, int bitRate, bool sound, bool keepOnDevice)
    {
        string[] audio = !sound ? ["--no-audio"] : keepOnDevice
            ? ["--audio-dup", "--no-audio-playback", "--audio-codec=aac", "--require-audio"]
            : ["--audio-codec=aac", "--require-audio"];
        return [$"--max-size={edge}", $"--max-fps={fps}", $"--video-bit-rate={bitRate}M", ..audio];
    }

    private FrameworkElement BuildCameraPanel()
    {
        var front = UiCheck("Front camera");
        var dimensions = UiField("Camera resolution", "", 162);
        var fps = UiField("Camera FPS", "30", 110);
        var microphone = UiCheck("Record microphone audio with camera video");
        var available = UiEditor("Supported cameras and resolutions", 140, readOnly: true);
        available.Visibility = Visibility.Collapsed;

        string[] Options(bool recordSound)
        {
            var size = dimensions.Text.Trim().ToLowerInvariant().Replace('×', 'x');
            if (size.Length > 0 && !Regex.IsMatch(size, @"^[1-9]\d{1,4}x[1-9]\d{1,4}$"))
                throw new ArgumentException("Enter the camera resolution as width x height without spaces, for example 1920x1080.");
            var result = new List<string>
            {
                "--video-source=camera", front.IsChecked == true ? "--camera-facing=front" : "--camera-facing=back",
                $"--camera-fps={ReadNumber(fps, "Camera FPS", 1, 240)}"
            };
            if (size.Length > 0) result.Add($"--camera-size={size}");
            result.AddRange(recordSound ? ["--audio-source=mic", "--no-audio-playback", "--audio-codec=aac", "--require-audio"] : ["--no-audio"]);
            return result.ToArray();
        }

        async Task Preview(bool switchCamera)
        {
            var device = RequireDevice();
            await RequireAndroid(device, 31, "Camera streaming");
            if (switchCamera) front.IsChecked = front.IsChecked != true;
            var options = Options(false);
            await StopCameraAsync(device);
            BeginStudio(device, "Live camera", options);
        }
        return UiCard(UiText("Camera preview", true), UiRow(front, microphone),
            UiRow(Labeled("Resolution W×H (blank for automatic)", dimensions), Labeled("Camera FPS", fps)),
            UiRow(UiAction("Open live preview", () => Preview(false), true), UiAction("Switch camera", () => Preview(true)),
                UiAction("Supported sizes", async () =>
                {
                    var device = RequireDevice();
                    await RequireAndroid(device, 31, "Camera streaming");
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    probe.CancelAfter(TimeSpan.FromSeconds(25));
                    var selected = front.IsChecked == true ? "front" : "back";
                    var result = await tools.RunScrcpyAsync(device, "EzAdb Camera Sizes",
                        ["--video-source=camera", $"--camera-facing={selected}", "--list-camera-sizes"], probe.Token);
                    result.RequireSuccess();
                    available.Text = result.Details;
                    available.Visibility = Visibility.Visible;
                })), available,
            UiRow(UiAction("Start camera recording", async () =>
                {
                    var device = RequireDevice();
                    await RequireAndroid(device, 31, "Camera video");
                    var options = Options(microphone.IsChecked == true);
                    if (ChooseMediaFile("Save camera video", "MP4 video (*.mp4)|*.mp4", "Camera", "mp4") is not { } destination) return;
                    await StopCameraAsync(device);
                    BeginStudio(device, "Camera video", options, destination);
                }),
                UiAction("Save photo", async () =>
                {
                    var device = RequireDevice();
                    await RequireAndroid(device, 31, "Camera photo");
                    var ffmpeg = AdbTools.FindFfmpeg() ?? throw new InvalidOperationException(
                        "Camera photos require FFmpeg. EzAdb looks for ffmpeg.exe in the tools folder and on Windows PATH.");
                    var options = Options(false);
                    if (ChooseMediaFile("Save camera photo", "PNG image (*.png)|*.png", "Photo", "png") is not { } destination) return;
                    var resumePreview = studioSessions.Any(session => session.Device.Serial == device.Serial && session.Kind == "Live camera");
                    await StopCameraAsync(device);
                    var temporary = Path.Combine(Path.GetTempPath(), $"EzAdb-Camera-{Guid.NewGuid():N}.mp4");
                    var png = Path.Combine(Path.GetDirectoryName(destination)!, $".EzAdb-{Guid.NewGuid():N}.png");
                    try
                    {
                        using var photo = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        photo.CancelAfter(TimeSpan.FromSeconds(30));
                        var capture = await tools.RunScrcpyAsync(device, "EzAdb Camera Photo",
                            [..options, "--time-limit=2", $"--record={temporary}"], photo.Token);
                        capture.RequireSuccess();
                        if (!IsFinalizedMp4(temporary)) throw new IOException("The camera did not return a valid video frame.");
                        (await AdbTools.ExtractFrameAsync(ffmpeg, temporary, png, photo.Token)).RequireSuccess();
                        File.Move(png, destination, overwrite: true);
                        Notify($"Camera photo saved: {destination}");
                    }
                    finally
                    {
                        File.Delete(temporary);
                        File.Delete(png);
                        if (resumePreview && !lifetime.IsCancellationRequested)
                            BeginStudio(device, "Live camera", options);
                    }
                })),
            UiText("Camera streaming requires Android 12 or later. Switching cameras or capture modes reopens the preview."));
    }

    private FrameworkElement BuildDisplayPanel(out Action<Device?> deviceChanged)
    {
        var width = UiField("Width", "1280", 110);
        var height = UiField("Height", "800", 110);
        var dpi = UiField("Density", "240", 110);
        var appLabel = UiText("No app selected.");
        Device? target = null;
        LaunchableApp? choice = null;
        bool withoutApp = false;
        deviceChanged = device =>
        {
            if (!Equals(target, device))
            {
                target = device;
                choice = null;
                withoutApp = false;
                appLabel.Text = "No app selected.";
            }
        };
        bool SelectApp(Device device)
        {
            if (PickAndroidApp(device, this) is not { } app || !Equals(CurrentReadyDevice, device)) return false;
            choice = app;
            withoutApp = false;
            appLabel.Text = app.Name;
            return true;
        }
        return UiCard(UiText("Separate Android display", true),
            UiRow(Labeled("Width (px)", width), Labeled("Height (px)", height), Labeled("Density (dpi)", dpi)),
            UiText("App to open on launch", true), appLabel,
            UiRow(UiAction("Choose app", () =>
            {
                SelectApp(RequireDevice());
                return Task.CompletedTask;
            }),
            UiAction("Without an app", () =>
            {
                choice = null;
                withoutApp = true;
                appLabel.Text = "The display will open without an app.";
                return Task.CompletedTask;
            })),
            UiAction("Open virtual display", async () =>
            {
                var device = RequireDevice();
                await RequireAndroid(device, 29, "Virtual displays");
                var dimensions = $"{ReadNumber(width, "Width", 160, 8192)}x{ReadNumber(height, "Height", 160, 8192)}/{ReadNumber(dpi, "Density", 72, 1000)}";
                if (!Equals(device, CurrentReadyDevice)) return;
                if (choice is null && !withoutApp && !SelectApp(device)) return;
                var package = choice?.Package;
                string[] options = [$"--new-display={dimensions}", "--no-audio", "--display-ime-policy=local"];
                BeginStudio(device, "Virtual display", package is null ? options : [..options, $"--start-app={package}"]);
            }, true),
            UiText("Requires Android 10 or later. Each display has its own window. Android determines which apps can open there."));
    }

    private async Task RequireAndroid(Device device, int minimum, string feature)
    {
        var output = (await AndroidShell(device, "getprop ro.build.version.sdk")).Trim();
        if (!int.TryParse(output, out var version)) throw new InvalidOperationException("Could not read the Android version.");
        var android = minimum switch { 29 => 10, 30 => 11, 31 => 12, 33 => 13, _ => minimum };
        if (version < minimum) throw new InvalidOperationException($"{feature} requires Android {android} or later (API {minimum}). The device reports API {version}.");
    }

    private string? ChooseMediaFile(string title, string filter, string prefix, string extension)
    {
        var dialog = new SaveFileDialog
        {
            Title = title, Filter = filter, AddExtension = true, DefaultExt = extension,
            FileName = $"EzAdb-{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}"
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void BeginStudio(Device device, string kind, string[] options, string? destination = null)
    {
        if (closingStudio || lifetime.IsCancellationRequested) return;
        if (kind != "Virtual display" && studioSessions.Any(session => session.Device.Serial == device.Serial && session.Kind == kind))
            throw new InvalidOperationException($"{device.DisplayName} already has this session running.");
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var session = new StudioSession(device, kind, cancellation);
        studioSessions.Add(session);
        session.Completion = RunStudioAsync(session, options, destination);
    }

    private async Task RunStudioAsync(StudioSession session, string[] options, string? destination)
    {
        var scratch = destination is null ? null : Path.Combine(Path.GetDirectoryName(destination)!, $".EzAdb-{Guid.NewGuid():N}.mp4");
        var title = $"EzAdb · {session.Kind} · {session.Device.DisplayName} · {Guid.NewGuid().ToString("N")[..5]}";
        var token = session.Cancellation.Token;
        try
        {
            if (scratch is not null) options = [..options, $"--record={scratch}"];
            var progress = new Progress<string>(line =>
            {
                if (studioSessions.Contains(session) && ParseDisplayId(line) is { } id)
                {
                    session.DisplayId = id;
                    CollectionViewRefresh();
                }
                if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) Log(line);
            });
            var result = await tools.RunScrcpyAsync(session.Device, title, options, token, progress,
                started: session.Kind == "Virtual display"
                    ? process => session.Macros = TrackMacroSession(process, session.Device, token, () => session.DisplayId)
                    : null);
            result.RequireSuccess();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ToolDependencyException error) { ReportDependency(error); }
        catch (Exception error) { ReportFailure(session.Kind, error); }
        finally
        {
            UntrackMacroSession(session.Macros);
            if (scratch is not null && destination is not null)
            {
                try
                {
                    if (IsFinalizedMp4(scratch))
                    {
                        File.Move(scratch, destination, overwrite: true);
                        Notify($"Recording saved: {destination}");
                    }
                    else if (File.Exists(scratch))
                    {
                        Notify($"The MP4 is incomplete. The raw file is at {scratch}.", true);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    ReportFailure($"Recording remains at {scratch}", error);
                }
            }
            studioSessions.Remove(session);
            session.Cancellation.Dispose();
        }
    }

    private void CollectionViewRefresh() =>
        System.Windows.Data.CollectionViewSource.GetDefaultView(studioSessions).Refresh();

    private async Task StopCameraAsync(Device device)
    {
        var existing = studioSessions.Where(session => session.Device.Serial == device.Serial &&
            session.Kind is "Live camera" or "Camera video").ToArray();
        foreach (var session in existing) session.Cancellation.Cancel();
        await Task.WhenAll(existing.Select(session => session.Completion));
    }

    private async void OnStudioClosing(object? sender, CancelEventArgs e)
    {
        if (studioSessions.Count == 0) return;
        e.Cancel = true;
        if (closingStudio) return;
        closingStudio = true;
        Workspace.IsEnabled = false;
        var open = studioSessions.ToArray();
        foreach (var session in open) session.Cancellation.Cancel();
        await Task.WhenAll(open.Select(session => session.Completion));
        // Defer the close even if a session ended synchronously during the Closing event.
        await Dispatcher.InvokeAsync(Close);
    }

    internal static int? ParseDisplayId(string line)
    {
        var match = Regex.Match(line, @"New display: .+ \(id=(\d+)\)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    internal static bool IsFinalizedMp4(string path)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        var data = false;
        var index = false;
        while (stream.Length - stream.Position >= 8)
        {
            var start = stream.Position;
            stream.ReadExactly(header);
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            var headerSize = 8;
            if (size == 1)
            {
                if (stream.Length - stream.Position < 8) return false;
                stream.ReadExactly(header);
                size = BinaryPrimitives.ReadInt64BigEndian(header);
                headerSize = 16;
            }
            else if (size == 0) size = stream.Length - start;
            if (size < headerSize || size > stream.Length - start) return false;
            if (size > headerSize)
            {
                data |= type == 0x6D646174;  // mdat
                index |= type == 0x6D6F6F76; // moov
            }
            stream.Position = start + size;
        }
        return data && index;
    }
}
