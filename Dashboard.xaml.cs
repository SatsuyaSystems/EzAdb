using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EzAdb;

internal sealed partial class Dashboard : Window
{
    internal sealed record ToolLocation(string Title, string Folder);

    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly Dictionary<string, CancellationTokenSource> mirrors = new();
    private readonly string settingsFile;
    private readonly string audioFile;

    private AdbTools tools;
    private Device[] knownDevices = [];
    private bool busy;
    private bool refreshing;
    private bool checkingTools;
    private bool updatingDevices;
    private bool keepAudioOnDevice;
    private string? refreshError;
    private string? adbIssue;
    private string? scrcpyIssue;
    private IInputElement? previousFocus;
    private CancellationTokenSource? setupCancellation;
    private CancellationTokenSource? qrCancellation;
    private Device? Selected => Devices.SelectedItem as Device;
    private bool MissingTools => adbIssue is not null || scrcpyIssue is not null;
    private bool ToolsOpen => ToolsOverlay.Visibility == Visibility.Visible;
    private bool QrOpen => QrOverlay.Visibility == Visibility.Visible;

    public Dashboard(bool discover = true, string? settingsDirectory = null, Func<Device, string, Task<string>>? readFiles = null)
    {
        allowDevicePolling = discover;
        this.readFiles = readFiles ?? ((device, directory) => AndroidShell(device, DeviceCommands.ListDirectory(directory)));
        loadFilesOnNavigation = discover || readFiles is not null;
        settingsDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EzAdb");
        settingsFile = Path.Combine(settingsDirectory, "tools.txt");
        audioFile = Path.Combine(settingsDirectory, "audio-output.txt");
        tools = new AdbTools(ReadSetting(settingsFile));
        keepAudioOnDevice = ReadSetting(audioFile) != "pc";
        InitializeComponent();
        LoadWifiProfiles();
        KeepAudioToggle.IsChecked = keepAudioOnDevice;
        KeepAudioToggle.Checked += OnAudioOutputChanged;
        KeepAudioToggle.Unchecked += OnAudioOutputChanged;
        DescribeAudioOutput();
        InitializeDevicePages();
        OverviewNav.IsChecked = true;
        timer.Tick += async (_, _) =>
        {
            if (!busy && !checkingTools && !QrOpen)
            {
                await RefreshAsync();
            }
        };
        if (discover)
        {
            Loaded += OnLoaded;
        }
        StateChanged += (_, _) =>
        {
            var maximized = WindowState == WindowState.Maximized;
            MaximizeButton.ToolTip = maximized ? "Wiederherstellen" : "Maximieren";
            System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, (string)MaximizeButton.ToolTip);
            MaximizeIcon.Data = Geometry.Parse(maximized ? "M 4,1 H 12 V 9 M 1,4 H 9 V 12 H 1 Z" : "M 1,1 H 11 V 11 H 1 Z");
        };
        Closed += (_, _) =>
        {
            timer.Stop();
            lifetime.Cancel();
        };
        UpdateState();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        checkingTools = true;
        try
        {
            var issues = await Task.WhenAll(tools.CheckAsync(false, lifetime.Token), tools.CheckAsync(true, lifetime.Token));
            ApplyToolIssues(issues[0], issues[1]);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        finally { checkingTools = false; }
        UpdateState();
        if (!ToolsOpen && MissingTools)
        {
            ShowTools();
        }
        timer.Start();
        await RefreshAsync();
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        OverviewPage.Visibility = Visible(OverviewNav.IsChecked == true);
        WifiPage.Visibility = Visible(WifiNav.IsChecked == true);
        ApkPage.Visibility = Visible(ApkNav.IsChecked == true);
        ScreenPage.Visibility = Visible(ScreenNav.IsChecked == true);
        var activity = ActivityNav.IsChecked == true;
        ActivityPage.Visibility = Visible(activity);
        PageScroll.Visibility = Visible(!activity);
        PageScroll.ScrollToTop();
        var nav = ((RadioButton)sender).Name;
        var toolPage = ShowDevicePage(nav);
        (PageTitle.Text, PageDescription.Text) = toolPage ?? (nav switch
        {
            nameof(WifiNav) => ("WLAN-Verbindung", "Kopple dein Android-Gerät und verwende ADB kabellos."),
            nameof(ApkNav) => ("APK-Installer", "Installiere Android-Apps auf dem ausgewählten Gerät."),
            nameof(ScreenNav) => ("Bildschirm", "Steuere dein Android-Gerät am PC oder erstelle einen Screenshot."),
            nameof(ActivityNav) => ("Aktivität", "Sieh nach, welche Aktionen ausgeführt wurden."),
            _ => ("Geräte", "Verbinde und verwalte deine Android-Geräte.")
        });
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    private Brush Paint(string key) => (Brush)FindResource(key);

    private void OnGoToWifi(object sender, RoutedEventArgs e)
    {
        WifiNav.IsChecked = true;
        PairAddress.Focus();
    }

    private void OnSelectedDevice(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingDevices)
        {
            UpdateState();
            RefreshDevicePageSelection();
        }
    }

    private void OnPickDevice(object sender, RoutedEventArgs e) => Devices.SelectedItem = ((Button)sender).DataContext;
    private async void OnReload(object sender, RoutedEventArgs e) => await RefreshAsync();

    internal void ApplyDevices(Device[] list)
    {
        var ordered = list.OrderBy(device => device.Serial, StringComparer.Ordinal)
            .ThenBy(device => device.TransportId, StringComparer.Ordinal).ToArray();
        if (knownDevices.SequenceEqual(ordered))
        {
            return;
        }

        var selected = Selected;
        var previousDevices = knownDevices;
        var autoSelect = knownDevices.Length == 0 && ordered.Length == 1;
        var index = Array.IndexOf(ordered, selected);
        if (index < 0 && selected is not null)
        {
            index = Array.FindIndex(ordered, device => device.Serial == selected.Serial);
        }
        knownDevices = ordered;
        updatingDevices = true;
        try
        {
            Devices.ItemsSource = ordered;
            DeviceRows.ItemsSource = ordered;
            Devices.SelectedIndex = autoSelect ? 0 : index;
        }
        finally { updatingDevices = false; }
        UpdateState();
        RefreshDevicePageSelection();
        DeviceListChanged(previousDevices);
    }

    private void UpdateState()
    {
        var selected = Selected;
        var ready = adbIssue is null && selected?.Ready == true;
        var inMirror = ready && mirrors.ContainsKey(selected!.Serial);
        InstallButton.IsEnabled = ScreenshotButton.IsEnabled = ready && !busy;
        MirrorButton.IsEnabled = !busy && (scrcpyIssue is not null || ready && !inMirror);
        MirrorButton.Content = scrcpyIssue is not null ? "scrcpy einrichten" : inMirror ? "Steuerung läuft" : "Bildschirm steuern";
        PairButton.IsEnabled = ConnectButton.IsEnabled = QrButton.IsEnabled = !busy && !checkingTools && adbIssue is null;
        ReloadButton.IsEnabled = !busy && adbIssue is null;
        Devices.IsEnabled = knownDevices.Length > 0;
        NoDevices.Visibility = Visible(knownDevices.Length == 0);
        AutoToolsButton.IsEnabled = ChooseToolsButton.IsEnabled = !busy && !checkingTools;

        var (title, status, description, fg, bg) = (adbIssue, refreshError, selected?.State) switch
        {
            (not null, _, _) => ("ADB einrichten", "Einrichtung nötig", "Öffne „Tools verwalten“, um ADB einzurichten.", "Amber", "AmberSoft"),
            (_, not null, _) => ("ADB antwortet nicht", "Verbindung prüfen", "Details zum letzten Fehler findest du unter Aktivität.", "Amber", "AmberSoft"),
            (_, _, "device") => (selected!.DisplayName, $"Verbunden · {selected.ConnectionLabel}",
                "Wähle links, was du mit deinem Gerät machen möchtest.", "Green", "SuccessSoft"),
            (_, _, "unauthorized") => (selected!.DisplayName, "Freigabe fehlt", "Bestätige die Debugging-Anfrage auf deinem Android-Gerät.", "Amber", "AmberSoft"),
            (_, _, "offline") => (selected!.DisplayName, "Offline", "Stelle die USB- oder WLAN-Verbindung erneut her.", "Amber", "AmberSoft"),
            (_, _, not null) => (selected!.DisplayName, selected.StatusLabel, "Prüfe den Status auf deinem Gerät.", "Amber", "AmberSoft"),
            _ when knownDevices.Length > 0 => ("Gerät auswählen", "Geräte erkannt", "Wähle aus, welches Gerät du verwenden möchtest.", "Accent", "AccentSoft"),
            _ => ("Kein Gerät verbunden", "Bereit zum Verbinden", "Schließe dein Android-Gerät per USB an oder richte WLAN-Debugging ein.", "Accent", "AccentSoft")
        };
        HeroTitle.Text = title;
        HeroStatus.Text = status;
        HeroDescription.Text = description;
        HeroStatus.Foreground = HeroDot.Fill = Paint(fg);
        HeroBadge.Background = Paint(bg);
        ApkHint.Text = ready ? $"Zielgerät: {selected!.ConnectedName}" : "Verbinde ein Android-Gerät, um APKs zu installieren.";
        ScreenHint.Text = ready ? $"Zielgerät: {selected!.ConnectedName}" : "Verbinde ein Android-Gerät, um den Bildschirm zu öffnen.";
        ToolWarning.Visibility = Visible(MissingTools);
        RenderWifiProfiles();
    }

    private async Task RefreshAsync()
    {
        if (refreshing || adbIssue is not null || lifetime.IsCancellationRequested)
        {
            return;
        }

        // Polling is invisible. Only publish changed device data or a changed error.
        refreshing = true;
        var currentTools = tools;
        try
        {
            var result = await currentTools.RunAdbAsync(["devices", "-l"], lifetime.Token);
            if (currentTools != tools || lifetime.IsCancellationRequested)
            {
                return;
            }
            result.RequireSuccess();
            var recovered = refreshError is not null;
            refreshError = null;
            ApplyDevices(AdbTools.ParseDevices(result.Output));
            if (recovered)
            {
                UpdateState();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) when (currentTools != tools || lifetime.IsCancellationRequested) { }
        catch (ToolDependencyException error) { ReportDependency(error); }
        catch (Exception error)
        {
            if (refreshError != error.Message)
            {
                refreshError = error.Message;
                Log($"Geräteliste: {error.Message}");
                ApplyDevices([]);
                UpdateState();
            }
        }
        finally { refreshing = false; }
    }

    private void Log(string message)
    {
        if (lifetime.IsCancellationRequested)
        {
            return;
        }
        EmptyActivity.Visibility = Visibility.Collapsed;
        ActivityLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        ActivityLog.ScrollToEnd();
    }

    private void Notify(string message, bool error = false, string? details = null)
    {
        if (lifetime.IsCancellationRequested)
        {
            return;
        }
        Feedback.Background = Paint(error ? "AmberSoft" : "AccentSoft");
        FeedbackText.Foreground = Paint(error ? "Amber" : "Ink");
        FeedbackText.Text = message;
        Feedback.Visibility = Visibility.Visible;
        Log(details ?? message);
    }

    private void OnDismissFeedback(object sender, RoutedEventArgs e) => Feedback.Visibility = Visibility.Collapsed;

    private async Task RunActionAsync(string label, Func<Task> action)
    {
        if (busy || checkingTools || lifetime.IsCancellationRequested)
        {
            return;
        }
        busy = true;
        UpdateState();
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ToolDependencyException error) { ReportDependency(error); }
        catch (Exception error) { ReportFailure(label, error); }
        finally
        {
            busy = false;
            if (!lifetime.IsCancellationRequested)
            {
                UpdateState();
            }
        }
    }

    private void ReportFailure(string label, Exception error)
    {
        var line = error.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "Unbekannter Fehler";
        Notify($"{label}: {line[..Math.Min(line.Length, 200)]}", true, $"{label}: {error.Message}");
    }

    private static bool IsApk(string file) => Path.GetExtension(file).Equals(".apk", StringComparison.OrdinalIgnoreCase) && File.Exists(file);

    private static string[] DropApks(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files && files.All(IsApk) ? files : [];

    private void OnApkDragOver(object sender, DragEventArgs e)
    {
        var accepted = InstallButton.IsEnabled && DropApks(e).Length > 0;
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        ApkOutline.Stroke = Paint(accepted ? "Accent" : "Line");
        e.Handled = true;
    }

    private void OnApkDragLeave(object sender, DragEventArgs e) => ApkOutline.Stroke = Paint("Line");

    private async void OnApkDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ApkOutline.Stroke = Paint("Line");
        if (InstallButton.IsEnabled)
        {
            await InstallAsync(Selected, DropApks(e));
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        var device = Selected;
        var dialog = new OpenFileDialog { Title = "APK-Dateien auswählen", Filter = "Android-Pakete (*.apk)|*.apk", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
        {
            await InstallAsync(device, dialog.FileNames);
        }
    }

    private Task InstallAsync(Device? device, string[] paths)
    {
        if (device is not { Ready: true } || paths.Length == 0)
        {
            return Task.CompletedTask;
        }
        return RunActionAsync("APK-Installation", async () =>
        {
            var installed = 0;
            foreach (var path in paths)
            {
                var file = Path.GetFileName(path);
                Notify($"Installiere {file} auf {device.DisplayName} …");
                try
                {
                    if (!IsApk(path))
                    {
                        throw new FileNotFoundException("APK-Datei nicht gefunden.");
                    }
                    var result = await RunOnDeviceAsync(device, ["install", "-r", path], 300);
                    result.RequireSuccess(output => output.Split('\n', StringSplitOptions.TrimEntries).Contains("Success"));
                    installed++;
                    Log($"Installiert: {file}");
                }
                catch (Exception error) when (error is not ToolDependencyException && !lifetime.IsCancellationRequested)
                {
                    ReportFailure(file, error);
                }
            }
            if (installed == paths.Length)
            {
                Notify(paths.Length == 1 ? "APK installiert." : $"{installed} APK-Dateien installiert.");
            }
            else if (paths.Length > 1)
            {
                Notify($"{installed} von {paths.Length} APK-Dateien installiert. Details stehen unter Aktivität.", true);
            }
        });
    }

    private Task<CommandResult> RunOnDeviceAsync(Device device, string[] arguments, int seconds = 20, bool binary = false)
        => tools.RunDeviceAsync(device, arguments, lifetime.Token, seconds, binary);

    private async void OnMirror(object sender, RoutedEventArgs e)
    {
        if (Selected is { Ready: true } device)
        {
            await MirrorDeviceAsync(device);
        }
        else if (scrcpyIssue is not null)
        {
            ShowTools();
        }
    }

    private async Task MirrorDeviceAsync(Device device)
    {
        if (scrcpyIssue is not null)
        {
            ShowTools();
            return;
        }
        if (mirrors.ContainsKey(device.Serial))
        {
            return;
        }
        Notify($"Öffne Bildschirmsteuerung für {device.DisplayName} …");
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var session = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                mirrors[device.Serial] = session;
                UpdateState();
                MacroSession? macros = null;
                try
                {
                    var result = await tools.MirrorAsync(device, keepAudioOnDevice, session.Token,
                        started: process => macros = TrackMacroSession(process, device, session.Token));
                    session.Token.ThrowIfCancellationRequested();
                    result.RequireSuccess();
                    Notify($"Bildschirmsteuerung für {device.DisplayName} beendet.");
                    break;
                }
                catch (OperationCanceledException) when (session.IsCancellationRequested)
                {
                    // An audio switch restarts the session after the old scrcpy window has exited.
                }
                finally { UntrackMacroSession(macros); }
            }
        }
        catch (ToolDependencyException error) { ReportDependency(error); }
        catch (Exception error) { ReportFailure("Bildschirmsteuerung", error); }
        finally
        {
            mirrors.Remove(device.Serial);
            if (!lifetime.IsCancellationRequested)
            {
                UpdateState();
            }
        }
    }

    private void OnAudioOutputChanged(object sender, RoutedEventArgs e)
    {
        keepAudioOnDevice = KeepAudioToggle.IsChecked == true;
        DescribeAudioOutput();
        foreach (var session in mirrors.Values.ToArray())
        {
            session.Cancel();
        }
        var message = keepAudioOnDevice ? "Ton bleibt am Android-Gerät." : "Ton wird am PC wiedergegeben.";
        if (mirrors.Count > 0)
        {
            message += " Die Bildschirmsteuerung wird kurz neu geöffnet.";
        }
        try
        {
            SaveSetting(audioFile, keepAudioOnDevice ? "device" : "pc");
            if (mirrors.Count > 0)
            {
                Notify(message);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Notify($"{message} Die Einstellung konnte nur für diese Sitzung übernommen werden.", true, error.Message);
        }
    }

    private void DescribeAudioOutput() => AudioOutputDescription.Text = keepAudioOnDevice
        ? "Der Ton läuft über Android oder die damit verbundenen Boxen."
        : "Die Bildschirmsteuerung gibt den Ton am Windows-PC wieder.";

    private async void OnScreenshot(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Ready: true } device)
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "PNG-Bild (*.png)|*.png", DefaultExt = "png",
            FileName = $"Android-{DateTime.Now:yyyy-MM-dd-HHmmss}.png"
        };
        if (dialog.ShowDialog(this) == true)
        {
            await RunActionAsync("Screenshot", async () =>
            {
                var result = await RunOnDeviceAsync(device, ["exec-out", "screencap", "-p"], binary: true);
                result.RequireSuccess();
                if (!result.Data.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                {
                    throw new InvalidOperationException("Android hat kein PNG-Bild geliefert.");
                }
                await File.WriteAllBytesAsync(dialog.FileName, result.Data, lifetime.Token);
                Notify("Screenshot gespeichert.", details: $"Screenshot gespeichert: {dialog.FileName}");
            });
        }
    }

    private async void OnPair(object sender, RoutedEventArgs e) => await NetworkAsync(true);
    private async void OnConnect(object sender, RoutedEventArgs e) => await NetworkAsync(false);
    private async void OnPairKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && PairButton.IsEnabled)
        {
            e.Handled = true;
            await NetworkAsync(true);
        }
    }

    private async void OnConnectKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ConnectButton.IsEnabled)
        {
            e.Handled = true;
            await NetworkAsync(false);
        }
    }

    private Task NetworkAsync(bool pairing)
    {
        var field = pairing ? PairAddress : ConnectAddress;
        var address = field.Text.Trim();
        if (!AdbTools.IsEndpoint(address))
        {
            Notify("Gib die Adresse mit Port ein, zum Beispiel 192.168.1.20:37123.", true);
            field.Focus();
            return Task.CompletedTask;
        }
        var code = PairCode.Password;
        if (pairing && (code.Length != 6 || !code.All(char.IsAsciiDigit)))
        {
            Notify("Gib den sechsstelligen Kopplungscode von Android ein.", true);
            PairCode.Focus();
            return Task.CompletedTask;
        }

        return RunActionAsync(pairing ? "WLAN-Kopplung" : "WLAN-Verbindung", async () =>
        {
            if (pairing)
            {
                PairCode.Clear();
                Notify("Kopplung läuft …");
                var response = await tools.PairAsync(address, code, lifetime.Token);
                Notify("Gerät gekoppelt. Falls es nicht erscheint, nutze die Verbindungsadresse aus WLAN-Debugging.");
                await RefreshAsync();
                if (!lifetime.IsCancellationRequested)
                {
                    RememberWifiPairing(response);
                }
            }
            else
            {
                await ConnectSavedWifiAsync(address);
            }
        });
    }

    private async void OnShowQr(object sender, RoutedEventArgs e) => await StartQrAsync();
    private async void OnRetryQr(object sender, RoutedEventArgs e) => await StartQrAsync();
    private void OnCloseQr(object sender, RoutedEventArgs e) => CloseQr();

    internal void ShowQrDialog(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        QrImage.Source = image;
        QrPlate.Visibility = Visibility.Visible;
        RetryQrButton.Visibility = Visibility.Collapsed;
        QrStatus.Foreground = Paint("Accent");
        QrStatus.Text = "Warte auf den Scan …";
        if (!QrOpen)
        {
            previousFocus = Keyboard.FocusedElement;
            Workspace.IsEnabled = false;
            QrOverlay.Visibility = Visibility.Visible;
        }
        CloseQrButton.Focus();
    }

    internal void CloseQr()
    {
        if (!QrOpen)
        {
            return;
        }
        var active = qrCancellation;
        qrCancellation = null;
        active?.Cancel();
        QrImage.Source = null;
        QrOverlay.Visibility = Visibility.Collapsed;
        RestoreWorkspaceFocus();
    }

    private async Task StartQrAsync()
    {
        if (qrCancellation is not null || busy || checkingTools || adbIssue is not null)
        {
            return;
        }
        using var session = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        session.CancelAfter(TimeSpan.FromMinutes(2));
        qrCancellation = session;
        var pairing = new QrPairing();
        try
        {
            ShowQrDialog(pairing.CreatePng());
            var progress = new Progress<string>(message =>
            {
                if (qrCancellation == session && !session.IsCancellationRequested)
                {
                    QrStatus.Text = message;
                }
            });
            var output = await pairing.PairAsync(tools, progress, session.Token);
            if (qrCancellation != session)
            {
                return;
            }
            QrImage.Source = null;
            QrPlate.Visibility = Visibility.Collapsed;
            QrStatus.Text = "Gerät gekoppelt. Warte auf die Verbindung …";
            var guid = QrPairing.ReadGuid(output);
            Device? matched = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await RefreshAsync();
                session.Token.ThrowIfCancellationRequested();
                matched = guid is null ? null : Array.Find(knownDevices, device => QrPairing.IsPairedDevice(device, guid));
                if (matched is not null || guid is null)
                {
                    break;
                }
                await Task.Delay(850, session.Token);
            }
            if (matched is not null)
            {
                Devices.SelectedItem = matched;
            }
            RememberWifiPairing(output);
            QrStatus.Text = matched is null
                ? "Gerät gekoppelt. Wenn es nicht erscheint, verwende unten die Verbindungsadresse von Android."
                : $"Mit {matched.DisplayName} verbunden.";
            QrStatus.Foreground = Paint("Green");
            Log(QrStatus.Text);
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            if (qrCancellation == session && !lifetime.IsCancellationRequested)
            {
                QrFailed("Der QR-Code ist abgelaufen. Erstelle einen neuen Code.");
            }
        }
        catch (ToolDependencyException error)
        {
            if (qrCancellation == session)
            {
                ReportDependency(error);
            }
        }
        catch (Exception error)
        {
            if (qrCancellation == session)
            {
                Log($"QR-Kopplung: {error.Message.Replace(pairing.Password, "[Code]", StringComparison.Ordinal)}");
                QrFailed("Kopplung fehlgeschlagen. Prüfe, ob beide Geräte im selben Netzwerk sind, oder verwende den Kopplungscode.");
            }
        }
        finally
        {
            if (qrCancellation == session)
            {
                qrCancellation = null;
            }
        }
    }

    private void QrFailed(string message)
    {
        QrImage.Source = null;
        QrPlate.Visibility = Visibility.Collapsed;
        QrStatus.Foreground = Paint("Amber");
        QrStatus.Text = message;
        RetryQrButton.Visibility = Visibility.Visible;
    }

    private static string? ReadSetting(string file)
    {
        try { return File.Exists(file) ? File.ReadAllText(file).Trim() : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal void ApplyToolIssues(string? adb, string? scrcpy)
    {
        adbIssue = adb;
        scrcpyIssue = scrcpy;
        if (adb is not null)
        {
            ApplyDevices([]);
        }
        UpdateState();
        if (ToolsOpen)
        {
            RenderTools();
        }
    }

    private void ReportDependency(ToolDependencyException error)
    {
        if (!lifetime.IsCancellationRequested)
        {
            ApplyToolIssues(error.Tool == "adb.exe" ? error.Message : adbIssue,
                            error.Tool == "scrcpy.exe" ? error.Message : scrcpyIssue);
            Log($"Tools: {error.Message}");
            ShowTools();
        }
    }

    internal void ShowTools()
    {
        CloseQr();
        RenderTools();
        if (ToolsOpen)
        {
            return;
        }
        previousFocus = Keyboard.FocusedElement;
        Workspace.IsEnabled = false;
        ToolsFeedback.Visibility = Visibility.Collapsed;
        ToolsOverlay.Visibility = Visibility.Visible;
        AutoToolsButton.Focus();
    }

    private void RenderTools()
    {
        ModalTitle.Text = MissingTools ? "Android-Tools fehlen" : "Android-Tools verwalten";
        ModalDescription.Text = MissingTools
            ? "EzAdb kann ADB, scrcpy und die benötigten Bibliotheken direkt herunterladen und einrichten."
            : "ADB verbindet deine Geräte. scrcpy öffnet die Bildschirmsteuerung.";
        ModalIcon.Background = Paint(MissingTools ? "AmberSoft" : "AccentSoft");
        ModalIconPath.Data = (Geometry)FindResource(MissingTools ? "WarningIcon" : "ToolsIcon");
        ModalIconPath.Stroke = Paint(MissingTools ? "Amber" : "Accent");
        RenderTool(AdbState, AdbDetail, adbIssue, "USB, WLAN, APKs und Screenshots");
        RenderTool(ScrcpyState, ScrcpyDetail, scrcpyIssue, "Live-Steuerung mit Maus und Tastatur");
        var locations = GetToolLocations(tools.Adb, tools.Scrcpy);
        ToolLocations.ItemsSource = locations;
        ToolLocations.Visibility = Visible(locations.Length > 0);
    }

    internal static ToolLocation[] GetToolLocations(string? adb, string? scrcpy)
    {
        var adbFolder = ToolFolder(adb);
        var scrcpyFolder = ToolFolder(scrcpy);
        if (adbFolder is not null && adbFolder.Equals(scrcpyFolder, StringComparison.OrdinalIgnoreCase))
        {
            return [new("Tool-Ordner", adbFolder)];
        }
        return [..new[] { new ToolLocation("ADB-Ordner", adbFolder ?? ""), new ToolLocation("scrcpy-Ordner", scrcpyFolder ?? "") }
            .Where(location => location.Folder.Length > 0)];
    }

    private static string? ToolFolder(string? executable)
    {
        if (executable is null)
        {
            return null;
        }
        try
        {
            executable = File.ResolveLinkTarget(executable, returnFinalTarget: true)?.FullName ?? executable;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return Path.GetDirectoryName(executable);
    }

    private void OnOpenToolFolder(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is not ToolLocation location || !Directory.Exists(location.Folder))
        {
            ShowToolsFeedback("Der Tool-Ordner wurde nicht gefunden.");
            return;
        }
        try { Process.Start(new ProcessStartInfo(location.Folder) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowToolsFeedback($"Der Ordner konnte nicht geöffnet werden: {error.Message}");
        }
    }

    private void RenderTool(TextBlock status, TextBlock detail, string? issue, string purpose)
    {
        status.Text = issue is not null ? "Einrichten" : "Bereit";
        status.Foreground = Paint(issue is not null ? "Amber" : "Green");
        detail.Text = issue ?? purpose;
    }

    private void OnTools(object sender, RoutedEventArgs e) => ShowTools();
    private void OnCloseTools(object sender, RoutedEventArgs e) => CloseTools();

    internal void CloseTools()
    {
        if (!ToolsOpen)
        {
            return;
        }
        setupCancellation?.Cancel();
        ToolsOverlay.Visibility = Visibility.Collapsed;
        RestoreWorkspaceFocus();
    }

    private void RestoreWorkspaceFocus()
    {
        Workspace.IsEnabled = true;
        if (previousFocus is not null)
        {
            Keyboard.Focus(previousFocus);
        }
        else
        {
            ToolsButton.Focus();
        }
        previousFocus = null;
    }

    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }
        e.Handled = QrOpen || ToolsOpen;
        CloseQr();
        CloseTools();
    }

    private async void OnAutoTools(object sender, RoutedEventArgs e)
    {
        var installRoot = Path.Combine(Path.GetDirectoryName(settingsFile)!, "tools");
        await ConfigureToolsAsync(async cancellation =>
        {
            var progress = new Progress<string>(message =>
            {
                if (!cancellation.IsCancellationRequested && ToolsOpen && ToolsProgress.Visibility == Visibility.Visible)
                {
                    ShowToolsFeedback(message, error: false);
                }
            });
            var bundled = Path.Combine(AppContext.BaseDirectory, "tools");
            return await ToolInstaller.ReadyAsync(bundled, cancellation)
                ?? await ToolInstaller.InstallAsync(installRoot, progress, cancellation);
        });
    }

    private async void OnChooseTools(object sender, RoutedEventArgs e)
    {
        if (checkingTools || busy)
        {
            return;
        }
        var dialog = new OpenFolderDialog { Title = "Entpackten Ordner mit ADB und scrcpy auswählen" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        var folder = dialog.FolderName;
        await ConfigureToolsAsync(async cancellation =>
            await ToolInstaller.ReadyAsync(folder, cancellation)
            ?? throw new InvalidOperationException("Wähle den vollständigen Ordner mit adb.exe, scrcpy.exe und den Bibliotheken."));
    }

    private async Task ConfigureToolsAsync(Func<CancellationToken, Task<AdbTools>> configure)
    {
        if (checkingTools || busy)
        {
            return;
        }
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        setupCancellation = setup;
        checkingTools = true;
        UpdateState();
        ToolsProgress.Visibility = Visibility.Visible;
        ShowToolsFeedback("Einrichtung läuft …", error: false);
        try
        {
            var candidate = await configure(setup.Token);
            setup.Token.ThrowIfCancellationRequested();
            tools = candidate;
            refreshError = null;
            ApplyDevices([]);
            ApplyToolIssues(null, null);
            ToolsProgress.Visibility = Visibility.Collapsed;
            try
            {
                SaveSetting(settingsFile, Path.GetDirectoryName(tools.Adb)!);
                ShowToolsFeedback("Einrichtung abgeschlossen.", error: false);
                Log("ADB und scrcpy sind bereit.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ShowToolsFeedback("Tools sind für diese Sitzung bereit. Der Ordnerpfad konnte nicht gespeichert werden.");
                Log(error.Message);
            }
            await RefreshAsync();
        }
        catch (OperationCanceledException) when (setup.IsCancellationRequested) { }
        catch (OperationCanceledException) { ShowToolsFeedback("Der Download hat zu lange gedauert. Versuche es erneut."); }
        catch (HttpRequestException error)
        {
            ShowToolsFeedback("Der Download ist fehlgeschlagen. Prüfe die Internetverbindung und versuche es erneut.");
            Log($"Tools: {error.Message}");
        }
        catch (Exception error) { ShowToolsFeedback(error.Message); }
        finally
        {
            setupCancellation = null;
            checkingTools = false;
            if (!lifetime.IsCancellationRequested)
            {
                ToolsProgress.Visibility = Visibility.Collapsed;
                UpdateState();
            }
        }
    }

    private static void SaveSetting(string file, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var pending = $"{file}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(pending, value);
            File.Move(pending, file, overwrite: true);
        }
        finally
        {
            File.Delete(pending);
        }
    }

    private void ShowToolsFeedback(string text, bool error = true)
    {
        ToolsFeedback.Foreground = Paint(error ? "Amber" : "Muted");
        ToolsFeedback.Text = text;
        ToolsFeedback.Visibility = Visibility.Visible;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
