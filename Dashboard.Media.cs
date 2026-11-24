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
        var sessions = UiList("Laufende Studio-Sitzungen", 116);
        sessions.ItemsSource = studioSessions;
        var displayActions = UiRow(
            UiAction("App für dieses Display auswählen", async () =>
            {
                var session = sessions.SelectedItem as StudioSession ??
                    (studioSessions.Where(item => item.DisplayId is not null).ToArray() is [var displaySession] ? displaySession : null);
                if (session is not { DisplayId: { } id })
                    throw new InvalidOperationException("Wähle ein gestartetes virtuelles Display aus.");
                if (PickAndroidApp(session.Device, this) is { } app && studioSessions.Contains(session))
                {
                    await AndroidShell(session.Device, Launch(app.Package, id));
                    Notify($"{app.Name} auf Display {id} geöffnet.");
                }
            }), UiAction("Makro-Steuerung öffnen", () =>
            {
                if (sessions.SelectedItem is not StudioSession { Macros: { } macros })
                    throw new InvalidOperationException("Wähle ein laufendes virtuelles Display aus.");
                ShowMacroControls(macros);
                return Task.CompletedTask;
            }));
        var tabs = UiRow();
        (string Label, FrameworkElement View)[] pages = [("Bildschirm aufnehmen", recording), ("Handy-Kamera", camera), ("Virtuelle Displays", display)];
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
        var tracker = UiCard(UiText("Laufende Studio-Sitzungen", true), sessions,
            UiRow(UiAction("Sitzung stoppen", async () =>
            {
                var session = sessions.SelectedItem as StudioSession ?? (studioSessions.Count == 1 ? studioSessions[0] : null);
                if (session is null) throw new InvalidOperationException("Wähle eine Sitzung aus.");
                session.Cancellation.Cancel();
                await session.Completion;
            })), displayActions);
        tracker.Visibility = Visible(studioSessions.Count > 0);
        studioSessions.CollectionChanged += (_, change) =>
        {
            tracker.Visibility = Visible(studioSessions.Count > 0);
            if (change.NewItems is { Count: > 0 } added) sessions.SelectedItem = added[0];
        };
        return new DevicePage(UiStack(tabs, host, tracker), "Studio", "Nimm Bildschirm oder Kamera auf und öffne eigenständige Android-Displays.", deviceChanged);
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
        var size = UiField("Maximale Kantenlänge", "1920", 140);
        var bitRate = UiField("Videoqualität in Mbit/s", "8", 140);
        var fps = UiField("Maximale FPS", "30", 110);
        var audio = UiCheck("Geräteaudio mit aufnehmen");
        return UiCard(UiText("Bildschirm als MP4 aufnehmen", true),
            UiRow(Labeled("Max. Kantenlänge (px)", size), Labeled("Qualität (Mbit/s)", bitRate), Labeled("FPS-Limit", fps)),
            UiRow(audio), UiText("Die Auflösung behält das Seitenverhältnis. Geräteaudio wird ab Android 11 unterstützt, bei Ton am Gerät ab Android 13."),
            UiAction("Aufnahme starten", async () =>
            {
                var device = RequireDevice();
                var edge = ReadNumber(size, "Kantenlänge", 160, 8192);
                var frames = ReadNumber(fps, "FPS", 1, 240);
                var mbps = ReadNumber(bitRate, "Qualität", 1, 200);
                var sound = audio.IsChecked == true;
                if (sound) await RequireAndroid(device, keepAudioOnDevice ? 33 : 30,
                    keepAudioOnDevice ? "Audioaufnahme mit Wiedergabe am Gerät" : "Audioaufnahme");
                if (ChooseMediaFile("Bildschirmaufnahme speichern", "MP4-Video (*.mp4)|*.mp4", "Anzeige", "mp4") is not { } destination) return;
                BeginStudio(device, "Bildschirmaufnahme", RecordingOptions(edge, frames, mbps, sound, keepAudioOnDevice), destination);
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
        var front = UiCheck("Frontkamera");
        var dimensions = UiField("Kameraauflösung", "", 162);
        var fps = UiField("Kamera-FPS", "30", 110);
        var microphone = UiCheck("Mikrofon im Kameravideo aufnehmen");
        var available = UiEditor("Unterstützte Kameras und Auflösungen", 140, readOnly: true);
        available.Visibility = Visibility.Collapsed;

        string[] Options(bool recordSound)
        {
            var size = dimensions.Text.Trim().ToLowerInvariant().Replace('×', 'x');
            if (size.Length > 0 && !Regex.IsMatch(size, @"^[1-9]\d{1,4}x[1-9]\d{1,4}$"))
                throw new ArgumentException("Gib die Kameraauflösung als Breite x Höhe ohne Leerzeichen ein, zum Beispiel 1920x1080.");
            var result = new List<string>
            {
                "--video-source=camera", front.IsChecked == true ? "--camera-facing=front" : "--camera-facing=back",
                $"--camera-fps={ReadNumber(fps, "Kamera-FPS", 1, 240)}"
            };
            if (size.Length > 0) result.Add($"--camera-size={size}");
            result.AddRange(recordSound ? ["--audio-source=mic", "--no-audio-playback", "--audio-codec=aac", "--require-audio"] : ["--no-audio"]);
            return result.ToArray();
        }

        async Task Preview(bool switchCamera)
        {
            var device = RequireDevice();
            await RequireAndroid(device, 31, "Kameraübertragung");
            if (switchCamera) front.IsChecked = front.IsChecked != true;
            var options = Options(false);
            await StopCameraAsync(device);
            BeginStudio(device, "Kamera live", options);
        }
        return UiCard(UiText("Kamera am PC", true), UiRow(front, microphone),
            UiRow(Labeled("Auflösung WxH (leer = automatisch)", dimensions), Labeled("Kamera-FPS", fps)),
            UiRow(UiAction("Live öffnen", () => Preview(false), true), UiAction("Kamera wechseln", () => Preview(true)),
                UiAction("Unterstützte Größen", async () =>
                {
                    var device = RequireDevice();
                    await RequireAndroid(device, 31, "Kameraübertragung");
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    probe.CancelAfter(TimeSpan.FromSeconds(25));
                    var selected = front.IsChecked == true ? "front" : "back";
                    var result = await tools.RunScrcpyAsync(device, "EzAdb Kameragrößen",
                        ["--video-source=camera", $"--camera-facing={selected}", "--list-camera-sizes"], probe.Token);
                    result.RequireSuccess();
                    available.Text = result.Details;
                    available.Visibility = Visibility.Visible;
                })), available,
            UiRow(UiAction("Kameravideo starten", async () =>
                {
                    var device = RequireDevice();
                    await RequireAndroid(device, 31, "Kameravideo");
                    var options = Options(microphone.IsChecked == true);
                    if (ChooseMediaFile("Kameravideo speichern", "MP4-Video (*.mp4)|*.mp4", "Kamera", "mp4") is not { } destination) return;
                    await StopCameraAsync(device);
                    BeginStudio(device, "Kameravideo", options, destination);
                }),
                UiAction("Foto speichern", async () =>
                {
                    var device = RequireDevice();
                    await RequireAndroid(device, 31, "Kamerafoto");
                    var ffmpeg = AdbTools.FindFfmpeg() ?? throw new InvalidOperationException(
                        "Kamerafotos benötigen FFmpeg. EzAdb sucht ffmpeg.exe im Tools-Ordner und im Windows-PATH.");
                    var options = Options(false);
                    if (ChooseMediaFile("Kamerafoto speichern", "PNG-Bild (*.png)|*.png", "Foto", "png") is not { } destination) return;
                    var resumePreview = studioSessions.Any(session => session.Device.Serial == device.Serial && session.Kind == "Kamera live");
                    await StopCameraAsync(device);
                    var temporary = Path.Combine(Path.GetTempPath(), $"EzAdb-Kamera-{Guid.NewGuid():N}.mp4");
                    var png = Path.Combine(Path.GetDirectoryName(destination)!, $".EzAdb-{Guid.NewGuid():N}.png");
                    try
                    {
                        using var photo = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        photo.CancelAfter(TimeSpan.FromSeconds(30));
                        var capture = await tools.RunScrcpyAsync(device, "EzAdb Kamerafoto",
                            [..options, "--time-limit=2", $"--record={temporary}"], photo.Token);
                        capture.RequireSuccess();
                        if (!IsFinalizedMp4(temporary)) throw new IOException("Von der Kamera wurde kein gültiger Videoframe geliefert.");
                        (await AdbTools.ExtractFrameAsync(ffmpeg, temporary, png, photo.Token)).RequireSuccess();
                        File.Move(png, destination, overwrite: true);
                        Notify($"Kamerafoto gespeichert: {destination}");
                    }
                    finally
                    {
                        File.Delete(temporary);
                        File.Delete(png);
                        if (resumePreview && !lifetime.IsCancellationRequested)
                            BeginStudio(device, "Kamera live", options);
                    }
                })),
            UiText("Kameraübertragung benötigt Android 12 oder neuer. Beim Wechsel der Kamera oder Aufnahmemethode wird die Vorschau neu geöffnet."));
    }

    private FrameworkElement BuildDisplayPanel(out Action<Device?> deviceChanged)
    {
        var width = UiField("Breite", "1280", 110);
        var height = UiField("Höhe", "800", 110);
        var dpi = UiField("Dichte", "240", 110);
        var appLabel = UiText("Keine App ausgewählt.");
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
                appLabel.Text = "Keine App ausgewählt.";
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
        return UiCard(UiText("Separates Android-Display", true),
            UiRow(Labeled("Breite (px)", width), Labeled("Höhe (px)", height), Labeled("Dichte (dpi)", dpi)),
            UiText("App beim Start", true), appLabel,
            UiRow(UiAction("App auswählen", () =>
            {
                SelectApp(RequireDevice());
                return Task.CompletedTask;
            }),
            UiAction("Ohne App", () =>
            {
                choice = null;
                withoutApp = true;
                appLabel.Text = "Das Display wird ohne App geöffnet.";
                return Task.CompletedTask;
            })),
            UiAction("Virtuelles Display öffnen", async () =>
            {
                var device = RequireDevice();
                await RequireAndroid(device, 29, "Virtuelle Displays");
                var dimensions = $"{ReadNumber(width, "Breite", 160, 8192)}x{ReadNumber(height, "Höhe", 160, 8192)}/{ReadNumber(dpi, "Dichte", 72, 1000)}";
                if (!Equals(device, CurrentReadyDevice)) return;
                if (choice is null && !withoutApp && !SelectApp(device)) return;
                var package = choice?.Package;
                string[] options = [$"--new-display={dimensions}", "--no-audio", "--display-ime-policy=local"];
                BeginStudio(device, "Virtuelles Display", package is null ? options : [..options, $"--start-app={package}"]);
            }, true),
            UiText("Ab Android 10. Jedes Display erhält ein eigenes Fenster. Welche Apps dort starten dürfen, entscheidet Android."));
    }

    private async Task RequireAndroid(Device device, int minimum, string feature)
    {
        var output = (await AndroidShell(device, "getprop ro.build.version.sdk")).Trim();
        if (!int.TryParse(output, out var version)) throw new InvalidOperationException("Die Android-Version konnte nicht gelesen werden.");
        var android = minimum switch { 29 => 10, 30 => 11, 31 => 12, 33 => 13, _ => minimum };
        if (version < minimum) throw new InvalidOperationException($"{feature} benötigt Android {android} oder neuer (API {minimum}). Das Gerät meldet API {version}.");
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
        if (kind != "Virtuelles Display" && studioSessions.Any(session => session.Device.Serial == device.Serial && session.Kind == kind))
            throw new InvalidOperationException($"Auf {device.DisplayName} läuft diese Sitzung bereits.");
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
                started: session.Kind == "Virtuelles Display"
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
                        Notify($"Aufnahme gespeichert: {destination}");
                    }
                    else if (File.Exists(scratch))
                    {
                        Notify($"MP4 unvollständig. Die Rohdatei liegt unter {scratch}.", true);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    ReportFailure($"Aufnahme bleibt unter {scratch}", error);
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
            session.Kind is "Kamera live" or "Kameravideo").ToArray();
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
