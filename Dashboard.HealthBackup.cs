using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using static EzAdb.DeviceCommands;
using Path = System.IO.Path;

namespace EzAdb;

internal sealed partial class Dashboard
{
    private DispatcherTimer? healthTimer;
    private string BackupRootFile => Path.Combine(LocalData, "backup-root.txt");

    internal sealed record HealthReading(double? Battery, double? Temperature, double? Cpu, double? RamUsed,
        double? RamTotal, double? StorageUsed, double? StorageTotal, string[] DeviceInfo);

    private const string HealthCommand =
        "printf '\\n@battery\\n'; dumpsys battery; " +
        "printf '\\n@cpu\\n'; dumpsys cpuinfo; " +
        "printf '\\n@ram\\n'; cat /proc/meminfo; " +
        "printf '\\n@storage\\n'; df -k /sdcard; " +
        "printf '\\n@info\\n'; getprop ro.product.manufacturer; getprop ro.product.model; " +
        "getprop ro.build.version.release; getprop ro.build.version.sdk; getprop ro.product.cpu.abi; " +
        "getprop ro.build.version.security_patch; uname -r; cat /proc/uptime; :";

    internal static HealthReading ParseHealth(string output)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var current = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('@')) sections[line[1..]] = current = [];
            else current.Add(line);
        }
        List<string> Section(string name) => sections.TryGetValue(name, out var lines) ? lines : [];
        static double? Number(string? text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
                                                             double.IsFinite(n) && n >= 0 ? n : null;
        static string? Field(List<string> lines, string key) => lines.FirstOrDefault(line => line.StartsWith(key + ":", StringComparison.Ordinal))
            ?.Split(':', 2)[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var battery = Section("battery");
        var level = Number(Field(battery, "level"));
        var scale = Number(Field(battery, "scale"));
        var temp = double.TryParse(Field(battery, "temperature"), CultureInfo.InvariantCulture, out var rawTemp) ? rawTemp / 10 : (double?)null;
        var cpu = Regex.Match(string.Join('\n', Section("cpu")), @"(?m)^\s*(\d+(?:\.\d+)?)%\s+TOTAL:");
        var ram = Section("ram");
        var totalRam = Number(Field(ram, "MemTotal"));
        var freeRam = Number(Field(ram, "MemAvailable"));
        var disks = Section("storage").Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var disk = disks.LastOrDefault(fields => fields.Length >= 5 && fields[4].EndsWith('%'));
        var info = Section("info").Take(8).ToArray();
        return new HealthReading(scale is > 0 ? level / scale * 100 : level, temp, cpu.Success ? Number(cpu.Groups[1].Value) : null,
            totalRam - freeRam, totalRam, disk is null ? null : Number(disk[2]), disk is null ? null : Number(disk[1]), info);
    }

    private DevicePage BuildHealthPage()
    {
        healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        var battery = Metric("Akkustand", 0, 100);
        var heat = Metric("Akkutemperatur", 0, 65);
        var cpu = Metric("CPU-Auslastung", 0, 100);
        var ram = Metric("RAM belegt", 0, 100);
        var storage = Metric("Speicher belegt", 0, 100);
        var system = UiText("Gerätedaten erscheinen nach der ersten Messung.");
        var status = UiText("Wähle ein Android-Gerät.");
        var paused = UiCheck("Live-Messung pausieren");
        var refreshingHealth = false;
        CancellationTokenSource? currentPoll = null;
        Device? monitored = null;
        var metrics = new[] { battery, heat, cpu, ram, storage };

        async Task Refresh()
        {
            if (refreshingHealth || monitored is not { } device ||
                paused.IsChecked == true || activeDevicePage != nameof(HealthNav)) return;
            refreshingHealth = true;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            currentPoll = cancellation;
            try
            {
                var output = await AndroidShell(device, HealthCommand, cancellation.Token, 12);
                if (!Equals(monitored, device) || cancellation.IsCancellationRequested || activeDevicePage != nameof(HealthNav)) return;
                var data = ParseHealth(output);
                UpdateMetric(battery, data.Battery, Format(data.Battery, "%"));
                UpdateMetric(heat, data.Temperature, Format(data.Temperature, "°C"));
                UpdateMetric(cpu, data.Cpu, Format(data.Cpu, "%"));
                UpdateMetric(ram, Percent(data.RamUsed, data.RamTotal), Usage(data.RamUsed, data.RamTotal));
                UpdateMetric(storage, Percent(data.StorageUsed, data.StorageTotal), Usage(data.StorageUsed, data.StorageTotal));
                var labels = new[] { "Hersteller", "Modell", "Android", "API", "ABI", "Sicherheitspatch", "Kernel", "Laufzeit" };
                system.Text = string.Join("  ·  ", data.DeviceInfo.Select((value, i) =>
                {
                    var display = i == 7 ? Uptime(value) : value;
                    return string.IsNullOrWhiteSpace(display) ? "" : $"{labels[i]}: {display}";
                }).Where(line => line.Length > 0));
                status.Text = $"Letzte Messung {DateTime.Now:HH:mm:ss} · {device.DisplayName}";
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (ToolDependencyException error) { ReportDependency(error); }
            catch (Exception error) { status.Text = $"Messung fehlgeschlagen: {error.Message}"; }
            finally { currentPoll = null; refreshingHealth = false; }
        }
        healthTimer.Tick += async (_, _) => await Refresh();
        paused.Checked += (_, _) => { currentPoll?.Cancel(); status.Text = "Live-Messung pausiert."; };
        paused.Unchecked += async (_, _) => { if (allowDevicePolling) await Refresh(); };
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        foreach (var metric in metrics) grid.Children.Add(metric.Card);
        return new DevicePage(UiStack(UiRow(paused), grid, UiCard(UiText("Geräteinformationen", true), system, status)),
            "Gerätemonitor", "Messwerte im Vier-Sekunden-Takt und Diagramme der letzten 38 Messungen.", device =>
            {
                if (!Equals(monitored, device))
                {
                    currentPoll?.Cancel();
                    monitored = device;
                    foreach (var metric in metrics) { metric.History.Clear(); metric.Line.Points.Clear(); metric.Value.Text = "—"; }
                    system.Text = "";
                }
                status.Text = device is null ? "Wähle ein Android-Gerät." : "Warte auf die nächste Messung.";
                if (device is not null && allowDevicePolling) { healthTimer.Start(); _ = Refresh(); }
                else healthTimer.Stop();
            });

        static string Format(double? value, string unit) => value is { } number ? $"{number:0.#} {unit}" : "Nicht verfügbar";
        static double? Percent(double? value, double? max) => max is > 0 ? value / max * 100 : null;
        static string Usage(double? value, double? max) => value is { } used && max is { } total
            ? $"{used / 1048576:0.0} / {total / 1048576:0.0} GB" : "Nicht verfügbar";
        static string Uptime(string value) => double.TryParse(value.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
            seconds is >= 0 and < 3_000_000_000 ? $"{TimeSpan.FromSeconds(seconds).TotalHours:0.0} h" : "";
    }

    private sealed record ChartMetric(Border Card, TextBlock Value, Polyline Line, List<double?> History, double Min, double Max);

    private ChartMetric Metric(string title, double minimum, double maximum)
    {
        var frame = new Canvas { Width = 300, Height = 54, Background = Paint("Canvas"), ClipToBounds = true };
        foreach (var y in new[] { 13, 27, 41 })
        {
            frame.Children.Add(new Line { X2 = 300, Y1 = y, Y2 = y, Stroke = Paint("Line"), StrokeThickness = 1 });
        }
        var line = new Polyline { Stroke = Paint("Accent"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        frame.Children.Add(line);
        var value = UiText("—", heading: true);
        var card = UiCard(UiText(title), value, new Viewbox { Height = 54, Stretch = Stretch.Fill, Child = frame });
        card.Margin = new Thickness(0, 0, 8, 9);
        return new ChartMetric(card, value, line, [], minimum, maximum);
    }

    private static void UpdateMetric(ChartMetric metric, double? number, string text)
    {
        metric.Value.Text = text;
        metric.History.Add(number);
        if (metric.History.Count > 38) metric.History.RemoveAt(0);
        var points = new PointCollection();
        for (var i = metric.History.Count - 1; i >= 0; i--)
        {
            if (metric.History[i] is not { } value) break;
            points.Add(new Point(298 - (metric.History.Count - i - 1) * 8,
                51 - Math.Clamp((value - metric.Min) / (metric.Max - metric.Min), 0, 1) * 48));
        }
        if (points.Count == 1) points.Add(new Point(points[0].X - 3, points[0].Y));
        metric.Line.Points = points;
    }

    private void StopHealthPolling() => healthTimer?.Stop();

    private sealed record BackupEntry(string Remote, string LocalFolder);
    private sealed record BackupManifest(string Device, DateTimeOffset Created, BackupEntry[] Entries, int Failures);
    private sealed record BackupChoice(string Folder, BackupManifest Manifest)
    {
        public string Display => $"{Manifest.Created.ToLocalTime():dd.MM.yyyy HH:mm}  ·  {Manifest.Device}  ·  {Manifest.Entries.Length} Quellen" +
                                (Manifest.Failures > 0 ? $"  ·  {Manifest.Failures} ausgelassen" : "");
    }

    private DevicePage BuildBackupPage()
    {
        var root = ReadSetting(BackupRootFile) ?? Path.Combine(LocalData, "Backups");
        var folderLabel = UiText(root);
        var categories = new[] { (UiCheck("Kamera (DCIM)", true), "/sdcard/DCIM"), (UiCheck("Bilder", true), "/sdcard/Pictures"),
            (UiCheck("Videos", true), "/sdcard/Movies"), (UiCheck("Downloads", true), "/sdcard/Download"),
            (UiCheck("Dokumente", true), "/sdcard/Documents"), (UiCheck("Musik"), "/sdcard/Music") };
        var custom = UiField("Weiterer Android-Pfad");
        var list = UiList("Gespeicherte Backups", 185, display: nameof(BackupChoice.Display));
        var status = UiText("Backups enthalten nur Dateien, auf die ADB Zugriff hat.");
        var contents = UiList("Quellen im ausgewählten Backup", 102, multiple: true, nameof(BackupEntry.Remote));

        void Reload()
        {
            folderLabel.Text = root;
            var choices = new List<BackupChoice>();
            if (Directory.Exists(root))
            {
                foreach (var folder in Directory.EnumerateDirectories(root, "EzAdb-*"))
                {
                    try
                    {
                        var file = Path.Combine(folder, "backup.json");
                        if (File.Exists(file) && JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(file)) is { } manifest)
                            choices.Add(new BackupChoice(folder, manifest));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
                }
            }
            list.ItemsSource = choices.OrderByDescending(item => item.Manifest.Created).ToArray();
        }

        BackupChoice Pick() => list.SelectedItem as BackupChoice ?? throw new InvalidOperationException("Wähle ein Backup aus.");
        list.SelectionChanged += (_, _) =>
        {
            contents.ItemsSource = (list.SelectedItem as BackupChoice)?.Manifest.Entries;
            contents.SelectAll();
        };

        async Task Create()
        {
            var device = RequireDevice();
            var paths = categories.Where(category => category.Item1.IsChecked == true).Select(category => category.Item2).ToList();
            if (!string.IsNullOrWhiteSpace(custom.Text))
            {
                var extra = PathOnDevice(custom.Text.Trim());
                if (extra == "/") throw new ArgumentException("Wähle einen konkreten zugänglichen Android-Ordner oder eine Datei.");
                paths.Add(extra);
            }
            var sources = paths.Distinct(StringComparer.Ordinal).ToArray();
            if (sources.Length == 0) throw new ArgumentException("Wähle mindestens eine Quelle.");
            var created = DateTimeOffset.Now;
            var folder = Path.Combine(root, $"EzAdb-{created:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
            Directory.CreateDirectory(folder);
            var entries = new List<BackupEntry>();
            var failures = 0;
            for (var i = 0; i < sources.Length; i++)
            {
                var source = sources[i];
                var localName = $"Quelle-{i + 1}";
                var destination = Path.Combine(folder, localName);
                Directory.CreateDirectory(destination);
                status.Text = $"Sichere {source} ({i + 1}/{sources.Length}) …";
                try
                {
                    await PullPathsAsync(device, [source], destination, askOverwrite: false);
                    entries.Add(new BackupEntry(source, localName));
                }
                catch (Exception error) when (error is not (OperationCanceledException or ToolDependencyException))
                {
                    failures++;
                    Log($"Backup · {source}: {error.Message}");
                }
                finally
                {
                    if (entries.Count > 0)
                    {
                        SaveSetting(Path.Combine(folder, "backup.json"),
                            JsonSerializer.Serialize(new BackupManifest(device.DisplayName, created, entries.ToArray(), failures)));
                    }
                }
            }
            Reload();
            status.Text = $"{entries.Count} von {sources.Length} Quellen gesichert · {folder}";
        }

        async Task Restore()
        {
            var backup = Pick();
            var device = RequireDevice();
            var selection = contents.SelectedItems.Cast<BackupEntry>().ToArray();
            if (selection.Length == 0) throw new InvalidOperationException("Markiere die Quellen, die wiederhergestellt werden sollen.");
            if (!Confirm($"{selection.Length} Quellen auf {device.DisplayName} wiederherstellen? Gleichnamige Dateien in den ursprünglichen Ordnern können überschrieben werden.", "Backup wiederherstellen")) return;
            foreach (var entry in selection)
                await RestorePath(device, BackupEntryPath(backup, entry), PathOnDevice(entry.Remote));
            status.Text = $"Wiederherstellung auf {device.DisplayName} abgeschlossen.";
        }

        async Task RestoreFile()
        {
            var backup = Pick();
            var device = RequireDevice();
            var dialog = new OpenFileDialog { Title = "Dateien aus diesem Backup wiederherstellen", InitialDirectory = backup.Folder, Multiselect = true };
            if (dialog.ShowDialog(this) != true) return;
            var selected = new List<(string Local, string Remote)>();
            foreach (var file in dialog.FileNames)
            {
                var match = backup.Manifest.Entries.Select(entry => (Entry: entry, Path: BackupEntryPath(backup, entry)))
                    .FirstOrDefault(item => file.Equals(item.Path, StringComparison.OrdinalIgnoreCase) ||
                        file.StartsWith(item.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                if (match.Entry is null) throw new IOException("Wähle Dateien aus den gesicherten Quellen dieses Backups.");
                var relative = file.Equals(match.Path, StringComparison.OrdinalIgnoreCase) ? "" : file[(match.Path.Length + 1)..];
                selected.Add((file, PathOnDevice(match.Entry.Remote + "/" + relative.Replace('\\', '/'))));
            }
            if (!Confirm($"{selected.Count} Dateien auf {device.DisplayName} wiederherstellen und gleichnamige Dateien überschreiben?", "Dateien wiederherstellen")) return;
            foreach (var (local, remote) in selected) await RestorePath(device, local, remote);
            status.Text = $"{selected.Count} Dateien wiederhergestellt.";
        }

        var view = UiStack(UiCard(UiText("Backup erstellen", true), UiRow(categories.Select(category => (UIElement)category.Item1).ToArray()),
                Labeled("Weitere Datei oder weiterer Ordner auf Android", custom), UiRow(UiAction("Backup erstellen", Create, true),
                    UiAction("Backup-Ordner ändern", () =>
                    {
                        if (BrowseDirectory("Backup-Ordner auswählen") is { } chosen)
                        {
                            root = chosen;
                            SaveSetting(BackupRootFile, root);
                            Reload();
                        }
                        return Task.CompletedTask;
                    })), folderLabel, status),
            UiCard(UiText("Gespeicherte Backups", true), list, contents,
                UiRow(UiAction("Quellen wiederherstellen", Restore, true), UiAction("Einzelne Dateien wiederherstellen", RestoreFile),
                    UiAction("Im Explorer öffnen", () =>
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Pick().Folder) { UseShellExecute = true });
                        return Task.CompletedTask;
                    }),
                    UiAction("Backup löschen", () =>
                    {
                        var backup = Pick();
                        if (Confirm($"Dieses lokale Backup löschen?\n{backup.Folder}", "Backup löschen"))
                        {
                            var path = Path.GetFullPath(backup.Folder);
                            if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase) ||
                                !Path.GetFileName(path).StartsWith("EzAdb-", StringComparison.Ordinal) ||
                                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                                throw new IOException("Dieser Ordner liegt außerhalb der verwalteten Backups.");
                            Directory.Delete(path, recursive: true);
                            Reload();
                        }
                        return Task.CompletedTask;
                    }))));
        Reload();
        return new DevicePage(view, "Backups", "Sichere zugängliche Android-Dateien und stelle sie einzeln oder nach Quelle wieder her.");
    }

    private static string BackupEntryPath(BackupChoice backup, BackupEntry entry) =>
        LocalChild(LocalChild(backup.Folder, entry.LocalFolder), Leaf(PathOnDevice(entry.Remote)));

    private async Task RestorePath(Device device, string local, string remote)
    {
        var parent = Parent(remote);
        await AndroidShell(device, $"mkdir -p {Quote(parent)}");
        var destination = Path.GetFileName(Path.TrimEndingDirectorySeparator(local)).Equals(Leaf(remote), StringComparison.Ordinal)
            ? parent.TrimEnd('/') + "/" : remote;
        (await DeviceCommand(device, ["push", local, destination], seconds: 0)).RequireSuccess();
        Log($"{device.DisplayName}: {remote} wiederhergestellt.");
    }
}
