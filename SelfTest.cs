using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EzAdb;

internal static class SelfTest
{
    public static int Run(string? previewFolder, string? incompleteToolsFolder, string? setupSource)
    {
        try
        {
            var devices = AdbTools.ParseDevices(
                "* daemon started successfully\r\nList of devices attached\r\n" +
                "usb-id\tdevice product:foo model:Pixel_8 transport_id:2\r\n" +
                "192.168.0.12:37621 offline transport_id:3\r\nserial unauthorized transport_id:4\r\n");
            if (devices is not [ { Serial: "usb-id", Ready: true, Model: "Pixel 8", TransportId: "2" },
                    { State: "offline", Ready: false }, { State: "unauthorized", Ready: false } ] ||
                !AdbTools.IsEndpoint("192.168.0.12:37621") || !AdbTools.IsEndpoint("[fe80::1]:5555") ||
                AdbTools.IsEndpoint("192.168.0.12") || AdbTools.IsEndpoint("android.local:65536") ||
                AdbTools.IsEndpoint("android.local:+5555") || AdbTools.IsEndpoint("-s:5555") ||
                !AdbTools.IsDependencyError(unchecked((int)0xC0000135)) || AdbTools.IsDependencyError(1))
            {
                return 1;
            }

            var tools = new AdbTools();
            if (tools.Adb is not null)
            {
                tools.RunAdbAsync(["version"], CancellationToken.None).GetAwaiter().GetResult()
                    .RequireSuccess(output => output.StartsWith("Android Debug Bridge", StringComparison.Ordinal));
            }
            if (tools.Scrcpy is not null && tools.CheckAsync(true, CancellationToken.None).GetAwaiter().GetResult() is not null)
            {
                return 3;
            }
            var missingAdb = "adb.exe was not found.";
            if (incompleteToolsFolder is not null)
            {
                var issue = new AdbTools(incompleteToolsFolder).CheckAsync(false, CancellationToken.None).GetAwaiter().GetResult();
                if (issue is null || !issue.Contains("0xC0000135", StringComparison.Ordinal))
                {
                    return 8;
                }
                missingAdb = issue;
            }
            var pairing = new QrPairing();
            var png = pairing.CreatePng();
            const string guid = "adb-pixel";
            var services = "List of discovered mdns services\r\n" +
                "studio-other\t_adb-tls-pairing._tcp\t192.168.0.11:44821\r\n" +
                $"{pairing.ServiceName}\t_adb-tls-connect._tcp\t192.168.0.12:44822\r\n" +
                $"{pairing.ServiceName}\t_adb-tls-pairing._tcp.\t192.168.0.12:44823\r\n";
            if (!png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                QrPairing.FindEndpoint(services, pairing.ServiceName) != "192.168.0.12:44823" ||
                QrPairing.FindEndpoint(services, "studio-unknown") is not null ||
                QrPairing.ReadGuid($"Enter pairing code: Successfully paired to 192.168.0.12:44823 [guid={guid}]") != guid ||
                !QrPairing.IsPairedDevice(devices[0] with { Serial = $"{guid}-abcd._adb-tls-connect._tcp" }, guid) ||
                QrPairing.IsPairedDevice(devices[0] with { Serial = $"{guid}2._adb-tls-connect._tcp" }, guid))
            {
                return 9;
            }
            if (previewFolder is not null && setupSource is not null)
            {
                CheckSetup(previewFolder, setupSource);
            }

            if (AdbTools.MirrorArguments(devices[0], true) is not ["--serial", "usb-id", "--window-title", _, "--no-audio"] ||
                AdbTools.MirrorArguments(devices[0], false) is not ["--serial", "usb-id", "--window-title", _])
            {
                return 12;
            }
            var settings = Directory.CreateTempSubdirectory("EzAdb-self-test-");
            var dashboard = new Dashboard(discover: false, settingsDirectory: settings.FullName, readFiles: (_, path) => Task.FromResult(path switch
            {
                "/sdcard" => "d\0DCIM\0d\0Download\0d\0Documents\0d\0Music\0d\0Pictures\0f\0Notes.txt\0",
                "/sdcard/Download" => "d\0Projects\0f\0Photo.jpg\0f\0Guide.pdf\0",
                "/sdcard/DCIM" => "d\0Camera\0",
                _ => ""
            })) { ShowInTaskbar = false, Opacity = 0 };
            try
            {
                dashboard.Show();
                if (previewFolder is not null)
                {
                    Directory.CreateDirectory(previewFolder);
                }
                CheckDeviceSuite();
                Capture(dashboard, previewFolder, "geraete");

                dashboard.ApplyDevices([devices[0]]);
                var picker = (ComboBox)dashboard.FindName("Devices");
                var items = picker.ItemsSource;
                var selectionEvents = 0;
                picker.SelectionChanged += (_, _) => selectionEvents++;
                dashboard.ApplyDevices([devices[0] with { }]);
                if (selectionEvents != 0 || !ReferenceEquals(items, picker.ItemsSource) || !Equals(picker.SelectedItem, devices[0]))
                {
                    return 4;
                }
                CheckWifiProfiles(dashboard, devices[0], settings.FullName, previewFolder);
                dashboard.Dispatcher.Invoke(() => CheckControlWindows(dashboard, devices[0], settings.FullName, previewFolder),
                    System.Windows.Threading.DispatcherPriority.Normal);

                foreach (var (nav, page, file) in new[] { ("WifiNav", "WifiPage", "wlan"), ("ApkNav", "ApkPage", "apk"),
                    ("ScreenNav", "ScreenPage", "bildschirm"), ("ActivityNav", "ActivityPage", "aktivitaet") })
                {
                    ((RadioButton)dashboard.FindName(nav)).IsChecked = true;
                    if (((FrameworkElement)dashboard.FindName(page)).Visibility != Visibility.Visible)
                    {
                        return 5;
                    }
                    Capture(dashboard, previewFolder, file);
                }

                foreach (var (nav, name) in new[] { ("FilesNav", "dateien"), ("AppsNav", "apps"), ("HealthNav", "monitor"),
                    ("MediaNav", "studio"), ("RemoteNav", "fernbedienung"), ("ConsoleNav", "konsole"), ("MacrosNav", "makros"),
                    ("BatchNav", "mehrere-geraete"), ("BackupNav", "backups") })
                {
                    ((RadioButton)dashboard.FindName(nav)).IsChecked = true;
                    var host = (ContentControl)dashboard.FindName("DeviceToolPage");
                    if (host.Visibility != Visibility.Visible || host.Content is not FrameworkElement)
                    {
                        return 18;
                    }
                    Capture(dashboard, previewFolder, name);
                }

                var audio = (ToggleButton)dashboard.FindName("KeepAudioToggle");
                if (audio.IsChecked != true)
                {
                    return 13;
                }
                audio.IsChecked = false;
                var reopened = new Dashboard(discover: false, settingsDirectory: settings.FullName);
                try
                {
                    if (((ToggleButton)reopened.FindName("KeepAudioToggle")).IsChecked != false)
                    {
                        return 14;
                    }
                }
                finally { reopened.Close(); }
                audio.IsChecked = true;
                if (File.ReadAllText(Path.Combine(settings.FullName, "audio-output.txt")) != "device")
                {
                    return 15;
                }

                ((RadioButton)dashboard.FindName("WifiNav")).IsChecked = true;
                dashboard.ShowQrDialog(png);
                var qrOverlay = (FrameworkElement)dashboard.FindName("QrOverlay");
                if (qrOverlay.Visibility != Visibility.Visible)
                {
                    return 10;
                }
                Capture(dashboard, previewFolder, "qr");
                dashboard.CloseQr();
                if (qrOverlay.Visibility != Visibility.Collapsed)
                {
                    return 11;
                }
                ((RadioButton)dashboard.FindName("OverviewNav")).IsChecked = true;
                dashboard.ApplyToolIssues(missingAdb, "A scrcpy dependency is missing.");
                dashboard.ShowTools();
                var overlay = (FrameworkElement)dashboard.FindName("ToolsOverlay");
                var workspace = (FrameworkElement)dashboard.FindName("Workspace");
                if (overlay.Visibility != Visibility.Visible || workspace.IsEnabled)
                {
                    return 6;
                }
                var toolFolders = ((ItemsControl)dashboard.FindName("ToolLocations")).Items.Cast<Dashboard.ToolLocation>().ToArray();
                if (tools.Adb is not null && tools.Scrcpy is not null && toolFolders.Length == 0)
                {
                    return 16;
                }
                var separate = Dashboard.GetToolLocations(Path.Combine(settings.FullName, "adb", "adb.exe"),
                    Path.Combine(settings.FullName, "scrcpy", "scrcpy.exe"));
                if (separate.Length != 2 || separate[0].Folder != Path.Combine(settings.FullName, "adb") ||
                    separate[1].Folder != Path.Combine(settings.FullName, "scrcpy"))
                {
                    return 17;
                }
                dashboard.ApplyToolIssues(null, null);
                Capture(dashboard, previewFolder, "tools");
                dashboard.CloseTools();
                if (overlay.Visibility != Visibility.Collapsed || !workspace.IsEnabled)
                {
                    return 7;
                }
            }
            finally
            {
                dashboard.Close();
                settings.Delete(recursive: true);
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 2;
        }
    }

    private static void CheckWifiProfiles(Dashboard dashboard, Device usb, string settingsDirectory, string? previewFolder)
    {
        const string service = "adb-pixel-A1B2C3._adb-tls-connect._tcp";
        var connections = AdbTools.ParseDevices("List of devices attached\n" +
            $"{usb.Serial}\tdevice usb:1-2 model:Pixel_8 transport_id:2\n" +
            $"{service}\tdevice model:Pixel_8 transport_id:5\n" +
            "[fe80::1]:5555\tdevice transport_id:6\n" +
            "emulator-5554\tdevice transport_id:7\n");
        if (connections is not [{ ConnectionLabel: "USB", UsbPath: "1-2" }, { ConnectionLabel: "Wi-Fi" },
                { ConnectionLabel: "Wi-Fi" }, { ConnectionLabel: "Emulator" }] ||
            usb.ConnectionLabel != "USB" || Dashboard.ConnectedWifiAddress($"already connected to {service}") != service ||
            Dashboard.ConnectedWifiAddress("failed to connect to 192.168.1.5:5555") is not null ||
            Dashboard.FindAdvertisedWifiAddress("adb-pixel-A1B2C3\t_adb-tls-pairing._tcp.\t192.168.1.5:43210\n" +
                "adb-pixel-A1B2C3\t_adb-tls-connect._tcp.\t192.168.1.5:47321\n", "adb-pixel._adb-tls-connect._tcp") != service)
        {
            throw new InvalidOperationException("Could not match ADB connection types and Wi-Fi addresses.");
        }
        var picker = (ComboBox)dashboard.FindName("Devices");
        dashboard.ApplyDevices([usb, connections[1]]);
        picker.SelectedItem = connections[1];
        if (((TextBlock)dashboard.FindName("HeroStatus")).Text != "Connected · Wi-Fi")
        {
            throw new InvalidOperationException("The device view is missing the Wi-Fi label.");
        }
        Capture(dashboard, previewFolder, "geraete-verbindungen");

        var rows = (ItemsControl)dashboard.FindName("SavedWifiRows");
        if (rows.Items.Cast<Dashboard.WifiRow>().ToArray() is not [{ Address: service, Connected: true, Action: "Disconnect" }])
        {
            throw new InvalidOperationException("The saved Wi-Fi list does not show the active transport.");
        }
        ((RadioButton)dashboard.FindName("WifiNav")).IsChecked = true;
        Capture(dashboard, previewFolder, "wlan-gespeichert");
        dashboard.ApplyDevices([usb]);
        if (rows.Items.Cast<Dashboard.WifiRow>().ToArray() is not [{ Address: service, Connected: false, Action: "Connect" }])
        {
            throw new InvalidOperationException("The Wi-Fi device is no longer saved after disconnecting.");
        }
        var reopened = new Dashboard(discover: false, settingsDirectory: settingsDirectory);
        try
        {
            var saved = (ItemsControl)reopened.FindName("SavedWifiRows");
            if (saved.Items.Cast<Dashboard.WifiRow>().ToArray() is not [{ Address: service, Connected: false, Name: "Pixel 8" }])
                throw new InvalidOperationException("The saved Wi-Fi device is missing after restart.");
        }
        finally { reopened.Close(); }
        picker.SelectedItem = usb;
        ((RadioButton)dashboard.FindName("OverviewNav")).IsChecked = true;
    }

    private static void CheckDeviceSuite()
    {
        static void Require(bool condition, string test)
        {
            if (!condition) throw new InvalidOperationException($"Device features: {test}");
        }

        Require(DeviceCommands.Quote("/sdcard/Bo'xes") == "'/sdcard/Bo'\\''xes'", "Escaping Android paths");
        Require(DeviceCommands.PathOnDevice("/sdcard//DCIM/../Download/") == "/sdcard/Download", "Path normalization");
        var files = DeviceCommands.ParseDirectory("/sdcard", "f\0Ablage mit Leerzeichen.txt\0d\0ÄÖÜ\nOrdner\0");
        Require(files is [{ IsDirectory: true, Name: "ÄÖÜ\nOrdner" }, { FullPath: "/sdcard/Ablage mit Leerzeichen.txt" }], "Android file list");
        Require(DeviceCommands.ParsePackages("package:org.demo.app\r\npackage:android\nbroken\n")
            is ["android", "org.demo.app"], "Installed packages");
        var names = DeviceCommands.ParseAppNames("[server] INFO: List of apps:\n* Settings                com.android.settings\n" +
            "- An app with a longer display name\n                               org.demo.app\n");
        Require(names.Count == 2 && names["com.android.settings"] == "Settings" &&
            names["org.demo.app"].EndsWith("display name", StringComparison.Ordinal), "App display names");
        Require(DeviceCommands.ParseAdbArguments("adb shell \"echo a b\"", true) is ["shell", "echo a b"], "ADB arguments");
        Require(DeviceCommands.TypeText("a %s\nb").Contains("input text '%'", StringComparison.Ordinal), "Text input with a percent sign");
        var steps = Dashboard.ParseMacro("tap 10 20\nswipe 1 2 3 4 350\napp com.android.settings\ntext Hello\nwait 150\nadb shell id");
        Require(steps.Length == 6 && steps[0].Command is ["shell", "-T", "input tap 10 20"] &&
            steps[4].WaitMs == 150 && steps[5].Command is ["shell", "id"], "Macro steps");
        var virtualSteps = Dashboard.ParseMacro("tap 10 20\nswipe 1 2 3 4 350\ntext Hello\nkey BACK\napp com.android.settings\nadb shell id", 8);
        Require(virtualSteps.Take(4).All(step => step.Command![2].StartsWith("input -d 8 ", StringComparison.Ordinal)) &&
            virtualSteps[4].Command![2].Contains("--display 8 ", StringComparison.Ordinal) &&
            virtualSteps[5].Command is ["shell", "id"], "Macros on a virtual display");
        var logical = Dashboard.ParsePointDisplay("Display: mDisplayId=0\n init=2400x1080 cur=1080x2400 app=1080x2220\n" +
            "Display: mDisplayId=8 (organized)\n init=800x400 cur=800x400 app=800x370", 8);
        var content = Dashboard.VideoBounds(new Size(600, 600), logical);
        Require(logical == new Size(800, 400) && content == new Rect(0, 150, 600, 300) &&
            Dashboard.AndroidPoint(new Point(300, 300), content, logical) == new Point(400, 200) &&
            Dashboard.AndroidPoint(new Point(20, 149), content, logical) is null &&
            Dashboard.AndroidPoint(new Point(600, 450), content, logical) is null, "Click points in the video preview");
        var clicks = Dashboard.ParseMacro("# EzAdb-Klickpunkte 800x400\npoint 10 20 125 0 3\npoint 70 90 400 600 2", 8);
        Require(clicks is [{ WaitMs: 125, Repeats: 3, Command: ["shell", "-T", "input -d 8 tap 10 20"] },
            { WaitMs: 400, Repeats: 2, Command: ["shell", "-T", "input -d 8 swipe 70 90 70 90 600"] }] &&
            clicks[1].PointDisplay == logical && clicks[1].DisplayId == 8, "Click rules for the selected display");
        var data = Dashboard.ParseHealth("@battery\nlevel: 79\nscale: 100\ntemperature: 325\n@cpu\n 4.2% TOTAL: 1% user\n" +
            "@ram\nMemTotal: 8000 kB\nMemAvailable: 2500 kB\n@storage\nfs 37000 25000 12000 68% /sdcard\n@info\nGoogle\nPixel\n16\n36\narm64-v8a\n");
        Require(data is { Battery: 79, Temperature: 32.5, Cpu: 4.2, RamUsed: 5500, StorageUsed: 25000 }, "Device monitor");
        Require(Dashboard.ParseDisplayId("[server] INFO: New display: 1280x800/240 (id=4)") == 4, "Virtual display ID");
        Require(Dashboard.RecordingOptions(1920, 30, 8, false, true).Contains("--no-audio") &&
            Dashboard.RecordingOptions(1920, 30, 8, true, true).Contains("--audio-dup"), "MP4 recording options");

        if (Environment.GetEnvironmentVariable("EZADB_OFFLINE_VIDEO") is { Length: > 0 } video)
        {
            Require(Dashboard.IsFinalizedMp4(video), "MP4 index");
            var ffmpeg = AdbTools.FindFfmpeg() ?? throw new InvalidOperationException("FFmpeg test tool was not found.");
            var png = Path.Combine(Path.GetTempPath(), $"EzAdb-Test-{Guid.NewGuid():N}.png");
            try
            {
                AdbTools.ExtractFrameAsync(ffmpeg, video, png, CancellationToken.None).GetAwaiter().GetResult().RequireSuccess();
                Require(File.ReadAllBytes(png).AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Camera photo export");
            }
            finally { File.Delete(png); }
        }
    }

    private static void CheckControlWindows(Dashboard dashboard, Device device, string settingsFolder, string? folder)
    {
        static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
        {
            foreach (var node in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                if (node is T item) yield return item;
                foreach (var child in Children<T>(node)) yield return child;
            }
        }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Exception? failure = null;

        dashboard.FilesNav.IsChecked = true;
        var filesView = (FrameworkElement)dashboard.DeviceToolPage.Content;
        var files = Children<ListView>(filesView).Single();
        var address = Children<TextBox>(filesView).Single(box =>
            System.Windows.Automation.AutomationProperties.GetName(box) == "Android path");
        var filter = Children<TextBox>(filesView).Single(box => box != address);
        void FileAction(string caption) => Click(Children<Button>(filesView).Single(button =>
            Equals(button.Content, caption) || Equals(button.ToolTip, caption)));
        void CheckFolder(string expected, int count)
        {
            if (address.Text != expected || files.Items.Count != count)
                throw new InvalidOperationException($"File manager displays {expected} incorrectly.");
        }
        CheckFolder("/sdcard", 6);
        FileAction("Downloads");
        CheckFolder("/sdcard/Download", 3);
        filter.Text = ".pdf";
        CheckFolder("/sdcard/Download", 1);
        FileAction("Back (Alt+Left)");
        CheckFolder("/sdcard", 6);
        FileAction("Forward (Alt+Right)");
        CheckFolder("/sdcard/Download", 3);
        FileAction("/sdcard");
        CheckFolder("/sdcard", 6);
        FileAction("Camera");
        CheckFolder("/sdcard/DCIM", 1);
        if (Children<Button>(filesView).Single(button => Equals(button.ToolTip, "Forward (Alt+Right)")).IsEnabled)
            throw new InvalidOperationException("Navigating to a new folder did not clear forward history.");
        dashboard.Dispatcher.BeginInvoke(new Action(() =>
        {
            var explorer = dashboard.OwnedWindows.Cast<Window>().Single();
            explorer.Opacity = 0;
            try
            {
                if (explorer.Title != "Android in File Explorer" ||
                    !Children<Button>(explorer).Any(button => Equals(button.Content, "Open File Explorer")))
                    failure = new InvalidOperationException("Missing File Explorer entry point.");
            }
            finally { explorer.Close(); }
        }));
        FileAction("File Explorer");
        if (failure is not null) throw failure;
        FileAction("Internal storage");

        dashboard.ScreenNav.IsChecked = true;
        Click(dashboard.OpenVirtualDisplaysButton);
        if (!Children<Button>(dashboard.DeviceToolPage).Any(button => Equals(button.Content, "Choose app")))
            throw new InvalidOperationException("App picker missing from screen tools.");
        Capture(dashboard, folder, "virtuelle-displays");

        Dashboard.LaunchableApp[] examples = [new("Music", "org.demo.audio"), new("Calendar", "org.demo.calendar")];
        var picked = dashboard.PickAndroidApp(device, dashboard, _ =>
        {
            var dialog = dashboard.OwnedWindows.Cast<Window>().Single();
            dialog.Opacity = 0;
            dialog.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var search = Children<TextBox>(dialog).Single();
                    var select = Children<Button>(dialog).Single(button => button.IsDefault);
                    search.Text = "unbekannt";
                    if (select.IsEnabled) throw new InvalidOperationException("An empty app selection was accepted.");
                    search.Text = "MUS";
                    Capture((FrameworkElement)dialog.Content, folder, "app-auswahl");
                    Click(select);
                }
                catch (Exception error) { failure = error; dialog.Close(); }
            }));
            return Task.FromResult(examples);
        });
        if (failure is not null || picked != examples[0])
            throw new InvalidOperationException("Select an app by display name.", failure);

        var host = new Window { ShowActivated = false, Opacity = 0, Width = 820, Height = 620, WindowStyle = WindowStyle.None };
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Dashboard.MacroSession? session = null;
        try
        {
            host.Show();
            session = dashboard.TrackMacroSession(process, device, CancellationToken.None, () => 8,
                id => id == 8 ? Task.FromResult(new Size(800, 400)) : throw new InvalidOperationException("Wrong display."));
            if (session.Controls is not null || Application.Current.Windows.Cast<Window>().Any(w => w.Title == "EzAdb Click Points"))
                throw new InvalidOperationException("Macro UI appeared automatically.");
            dashboard.ScreenNav.IsChecked = true;
            Click(dashboard.MacroControlsButton);
            var controls = session.Controls ?? throw new InvalidOperationException("The app did not open a macro window.");
            controls.Opacity = 0;
            if (controls.Owner != dashboard) throw new InvalidOperationException("The macro window is not owned by the app.");
            Click(dashboard.MacroControlsButton);
            if (session.Controls != controls || dashboard.OwnedWindows.Cast<Window>().Count(w => w.Title == controls.Title) != 1)
                throw new InvalidOperationException("The macro window opened twice.");
            var palette = (FrameworkElement)controls.Content;
            Button ByLabel(DependencyObject root, string label) =>
                Children<Button>(root).Single(button => Equals(button.Content, label));
            controls.Dispatcher.BeginInvoke(new Action(() =>
            {
                var editor = controls.OwnedWindows.Cast<Window>().Single();
                editor.Opacity = 0;
                try
                {
                    TextBox Field(string label) => Children<TextBox>(editor).Single(field =>
                        System.Windows.Automation.AutomationProperties.GetName(field) == label);
                    Field("Macro name").Text = "Short pause";
                    Click(ByLabel(editor, "Wait"));
                    Field("Value for this macro step").Text = "0";
                    Click(ByLabel(editor, "Add step"));
                    Click(ByLabel(editor, "Save macro"));
                }
                catch (Exception error) { failure = error; }
                finally { if (editor.IsVisible) editor.Close(); }
            }));
            Click(ByLabel(palette, "New macro"));
            if (failure is not null || Children<ListBox>(palette).Single().SelectedItem as string != "Short pause")
                throw new InvalidOperationException("Save a macro in the macro window.", failure);
            Click(ByLabel(palette, "Run"));
            if (!Children<TextBlock>(palette).Any(label => label.Text.EndsWith("Macro complete", StringComparison.Ordinal)))
                throw new InvalidOperationException("Run a local wait macro in the macro window.");
            Capture(palette, folder, "makro-overlay");
            Click(ByLabel(palette, "Set click points"));
            Window FindPointEditor() => Application.Current.Windows.Cast<Window>().Single(w => w.Title == "EzAdb Click Points");
            var editorWindow = FindPointEditor();
            editorWindow.Opacity = 0;
            editorWindow.UpdateLayout();
            if (controls.IsVisible || !Children<Canvas>(editorWindow).Any() || editorWindow.ActualWidth <= 330 ||
                new System.Windows.Interop.WindowInteropHelper(editorWindow).Owner != process.MainWindowHandle)
                throw new InvalidOperationException("The click point editor does not cover the preview.");
            Click(ByLabel(editorWindow, "Back"));
            dashboard.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            if (!controls.IsVisible || editorWindow.IsVisible) throw new InvalidOperationException("The macro window did not return after point editing.");
            var pointFile = Path.Combine(settingsFolder, "macros", "Click sequence.txt");
            File.WriteAllText(pointFile, "# EzAdb-Klickpunkte 800x400\npoint 200 80 50 0 1\npoint 500 260 0 200 3");
            controls.Close();
            Click(dashboard.MacroControlsButton);
            controls = session.Controls ?? throw new InvalidOperationException("Could not reopen the macro window.");
            controls.Opacity = 0;
            palette = (FrameworkElement)controls.Content;
            Children<ListBox>(palette).Single().SelectedItem = "Click sequence";
            Click(ByLabel(palette, "Edit"));
            editorWindow = FindPointEditor();
            editorWindow.Opacity = 0;
            editorWindow.UpdateLayout();
            void InDialog(Action<Window> interact) => editorWindow.Dispatcher.BeginInvoke(new Action(() =>
            {
                var dialog = editorWindow.OwnedWindows.Cast<Window>().Single();
                dialog.Opacity = 0;
                try { interact(dialog); }
                catch (Exception error) { failure = error; }
                finally { if (dialog.IsVisible) dialog.Close(); }
            }));
            InDialog(dialog =>
            {
                void Set(string field, string value) => Children<TextBox>(dialog).Single(box =>
                    System.Windows.Automation.AutomationProperties.GetName(box) == field).Text = value;
                Set("Delay", "650");
                Set("Hold time", "300");
                Set("Repeat count", "4");
                Set("Order", "2");
                Click(ByLabel(dialog, "Apply"));
            });
            Click(ByLabel(editorWindow, "1"));
            Capture((FrameworkElement)editorWindow.Content, folder, "klickpunkte");
            InDialog(dialog => Click(ByLabel(dialog, "Save macro")));
            Click(ByLabel(editorWindow, "Save"));
            dashboard.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            if (failure is not null || File.ReadAllLines(pointFile) is not ["# EzAdb click points 800x400",
                "point 500 260 0 200 3", "point 200 80 650 300 4"])
                throw new InvalidOperationException("Edit, reorder, and save click points.", failure);
        }
        finally
        {
            dashboard.UntrackMacroSession(session);
            host.Close();
        }
    }

    private static void CheckSetup(string previewFolder, string setupSource)
    {
        var root = Path.Combine(previewFolder, $"install-test-{Guid.NewGuid():N}");
        var installed = setupSource == "download"
            ? ToolInstaller.InstallAsync(root, null, CancellationToken.None).GetAwaiter().GetResult()
            : ToolInstaller.InstallArchiveAsync(File.ReadAllBytes(setupSource), root, null, CancellationToken.None).GetAwaiter().GetResult();
        var cached = ToolInstaller.InstallAsync(root, null, CancellationToken.None).GetAwaiter().GetResult();
        if (installed.Adb is null || installed.Scrcpy is null || installed.Adb != cached.Adb ||
            installed.Scrcpy != cached.Scrcpy || Directory.EnumerateDirectories(root, ".setup-*").Any())
        {
            throw new InvalidOperationException("Test installation was not reused.");
        }
        try
        {
            ToolInstaller.InstallArchiveAsync([0, 1, 2], root, null, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("A package with an invalid checksum was accepted.");
    }

    private static void Capture(Dashboard dashboard, string? folder, string name) => Capture(dashboard.Root, folder, name);

    private static void Capture(FrameworkElement root, string? folder, string name)
    {
        if (folder is null)
        {
            return;
        }
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, $"{name}.png"));
        png.Save(file);
    }
}
