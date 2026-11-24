using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using static EzAdb.DeviceCommands;

namespace EzAdb;

internal sealed partial class Dashboard
{
    private ListBox? batchDevices;
    private readonly HashSet<string> automaticRuns = new();
    private string MacroDirectory => Path.Combine(LocalData, "macros");
    private string AutomaticMacroFile => Path.Combine(LocalData, "autostart-macro.txt");
    private string MacroPath(string label) => LocalChild(MacroDirectory, label.Trim() + ".txt");

    private string[] SavedMacroNames() => Directory.Exists(MacroDirectory)
        ? Directory.EnumerateFiles(MacroDirectory, "*.txt").Select(file => Path.GetFileNameWithoutExtension(file))
            .Order(StringComparer.CurrentCultureIgnoreCase).ToArray() : [];

    private void SaveMacro(string label, string description)
    {
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("Enter a name for this macro.");
        _ = ParseMacro(description);
        SaveSetting(MacroPath(label), description);
    }

    private DevicePage BuildRemotePage()
    {
        var text = UiField("Text for Android");
        Task Key(int code) => Send($"input keyevent {code}");
        async Task Send(string command)
        {
            var device = RequireDevice();
            await AndroidShell(device, command);
            Log($"{device.DisplayName}: Remote control · {command.Split(' ', 2)[0]}");
        }
        var controls = UiCard(UiText("Navigation", heading: true),
            UiRow(UiAction("Home", () => Key(3), true), UiAction("Back", () => Key(4)),
                UiAction("Recent apps", () => Key(187)), UiAction("Notifications", () => Send("cmd statusbar expand-notifications"))),
            UiText("Volume and power", heading: true),
            UiRow(UiAction("Volume down", () => Key(25)), UiAction("Volume up", () => Key(24)),
                UiAction("Mute", () => Key(164)), UiAction("Power", () => Key(26)),
                UiAction("Lock screen", () => Key(223)), UiAction("Restart", async () =>
                {
                    var device = RequireDevice();
                    if (Confirm($"Restart {device.DisplayName} now?", "Restart Android"))
                    {
                        (await DeviceCommand(device, ["reboot"])).RequireSuccess();
                        Notify($"Restart for {device.DisplayName} triggered.");
                    }
                })),
            UiText("Locking uses the delay configured on Android."),
            Labeled("Text for Android", text), UiAction("Send text", () => Send(TypeText(text.Text)), true),
            UiText("Tap the target field on Android first. ADB text input supports ASCII."));
        return new DevicePage(controls, "Remote control", "Control the selected device with ADB.");
    }

    private DevicePage BuildConsolePage()
    {
        var command = UiField("ADB command");
        var output = UiEditor("Live output", 335, readOnly: true);
        var perDevice = UiCheck("Run on selected device", true);
        var history = new List<string>();
        var historyIndex = 0;
        CancellationTokenSource? active = null;
        var stop = UiAction("Cancel", () => { active?.Cancel(); return Task.CompletedTask; });

        async Task Run()
        {
            if (active is not null) return;
            var line = command.Text.Trim();
            var args = ParseAdbArguments(line, perDevice.IsChecked == true);
            var device = perDevice.IsChecked == true ? RequireDevice() : null;
            if (history.LastOrDefault() != line)
            {
                history.Add(line);
                if (history.Count > 150) history.RemoveAt(0);
            }
            historyIndex = history.Count;
            using var session = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            active = session;
            AppendOutput(output, $"{(device is null ? "$ adb" : $"[{device.DisplayName}] adb")} {line}");
            try
            {
                var progress = new Progress<string>(value => AppendOutput(output, value));
                var result = device is null
                    ? await tools.RunAdbAsync(args, session.Token, seconds: 0, progress: progress)
                    : await DeviceCommand(device, args, session.Token, seconds: 0, output: progress);
                AppendOutput(output, $"[Exit {result.ExitCode}]");
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
                AppendOutput(output, "[Canceled]");
            }
            finally { active = null; }
        }

        command.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await RunPageTask("ADB command", Run);
            }
            else if (history.Count > 0 && e.Key is Key.Up or Key.Down)
            {
                e.Handled = true;
                historyIndex = Math.Clamp(historyIndex + (e.Key == Key.Up ? -1 : 1), 0, history.Count);
                command.Text = historyIndex < history.Count ? history[historyIndex] : "";
                command.CaretIndex = command.Text.Length;
            }
        };
        Button Quick(string title, string args, bool targeted = true) => UiAction(title, () =>
        {
            perDevice.IsChecked = targeted;
            command.Text = args;
            command.Focus();
            return Task.CompletedTask;
        });
        var view = UiCard(UiRow(Quick("Devices", "devices -l", false), Quick("Battery", "shell dumpsys battery"),
                Quick("Device info", "shell getprop"), Quick("Logcat", "logcat -d -t 200")),
            output, Labeled("ADB command", command), UiRow(perDevice, UiAction("Run", Run, true), stop,
                UiAction("Clear output", () => { output.Clear(); return Task.CompletedTask; })),
            UiText("Use Up and Down to browse this session’s history. Clear the device selection to run global ADB commands."));
        return new DevicePage(view, "ADB console", "Run ADB commands and view their output live.");
    }

    internal sealed record MacroStep(string[]? Command, int WaitMs = 0, int Repeats = 1, Size? PointDisplay = null, int? DisplayId = null);

    internal static MacroStep[] ParseMacro(string text, int? display = null)
    {
        var steps = new List<MacroStep>();
        var input = InputPrefix(display);
        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var pointDisplay = PointMacroSize(lines[0]);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var separator = line.IndexOfAny([' ', '\t']);
            var verb = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? "" : line[(separator + 1)..];
            try
            {
                steps.Add(verb.ToLowerInvariant() switch
                {
                    "adb" => new(ParseAdbArguments(value, deviceOnly: true)),
                    "tap" => new(Shell($"{input} tap " + Numbers(value, 2))),
                    "point" => ClickPoint.Parse(value, pointDisplay).Step(display, pointDisplay),
                    "swipe" => new(Shell($"{input} swipe " + Numbers(value, 5))),
                    "app" => new(Shell(Launch(value, display))),
                    "text" => new(Shell(TypeText(value, display))),
                    "key" => new(Shell($"{input} keyevent " + Quote(KeyCode(value)))),
                    "wait" when int.TryParse(value, out var ms) && ms is >= 0 and <= 600_000 => new(null, ms),
                    _ => throw new ArgumentException("Use adb, tap, point, swipe, app, text, key, or wait.")
                });
            }
            catch (ArgumentException error)
            {
                throw new ArgumentException($"Line {i + 1}: {error.Message}", error);
            }
        }
        if (steps.Count == 0) throw new ArgumentException("The macro has no steps.");
        return steps.ToArray();

        static string Numbers(string input, int count)
        {
            var fields = input.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != count || fields.Any(field => !int.TryParse(field, out var n) || n < 0))
                throw new ArgumentException($"Enter {count} non-negative integers.");
            return string.Join(' ', fields.Select(field => int.Parse(field).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        static string KeyCode(string value)
        {
            var key = value.Trim().ToUpperInvariant();
            if (key.Length == 0 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                throw new ArgumentException("Enter an Android key such as HOME or BACK.");
            return key;
        }
    }

    private async Task RunMacroAsync(Device device, MacroStep[] steps, CancellationToken cancellation, Action<string> onStatus)
    {
        if (steps.FirstOrDefault(step => step.PointDisplay.HasValue) is { PointDisplay: { } expected } first &&
            await ReadPointDisplay(device, first.DisplayId, cancellation) != expected)
            throw new InvalidOperationException("The display size has changed. Restore the original resolution and orientation.");
        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            for (int repeat = 1; repeat <= step.Repeats; repeat++)
            {
                cancellation.ThrowIfCancellationRequested();
                var label = step.PointDisplay.HasValue ? "Point" : "Step";
                onStatus($"{device.DisplayName} · {label} {i + 1}/{steps.Length}" + (step.Repeats > 1 ? $" · {repeat}/{step.Repeats}" : ""));
                if (step.WaitMs > 0) await Task.Delay(step.WaitMs, cancellation);
                if (step.Command is not null)
                    DeviceCommands.CheckAndroid(await DeviceCommand(device, step.Command, cancellation, seconds: 60));
            }
        }
        onStatus($"{device.DisplayName} · Macro complete");
    }

    private DevicePage BuildMacrosPage()
    {
        var name = UiField("Macro name");
        var editor = UiEditor("Macro steps", 225);
        var saved = UiList("Saved macros", 105);
        var status = UiText("");
        var automatic = UiText("");
        CancellationTokenSource? active = null;

        void Reload()
        {
            var selected = saved.SelectedItem as string;
            saved.ItemsSource = SavedMacroNames();
            saved.SelectedItem = selected;
            automatic.Text = ReadSetting(AutomaticMacroFile) is { Length: > 0 } label
                ? $"Autorun on connection: {label}" : "Autorun on connection is off.";
        }
        void Save()
        {
            SaveMacro(name.Text, editor.Text);
            Reload();
            status.Text = "Macro saved.";
        }
        saved.MouseDoubleClick += async (_, _) => await RunPageTask("Open macro", () =>
        {
            if (saved.SelectedItem is string label)
            {
                name.Text = label;
                editor.Text = File.ReadAllText(MacroPath(label));
            }
            return Task.CompletedTask;
        });
        var view = UiCard(Labeled("Macro name", name), editor,
            UiText("One step per line. Examples: tap 320 640 · swipe 100 900 100 300 400 · app com.android.settings · text Hello · wait 1000 · key HOME"),
            UiRow(UiAction("Run", async () =>
            {
                if (active is not null) return;
                var device = RequireDevice();
                var steps = ParseMacro(editor.Text);
                using var run = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                active = run;
                try { await RunMacroAsync(device, steps, run.Token, line => status.Text = line); }
                catch (OperationCanceledException) when (run.IsCancellationRequested) { status.Text = "Macro canceled."; }
                finally { active = null; }
            }, true), UiAction("Cancel", () => { active?.Cancel(); return Task.CompletedTask; }),
                UiAction("Save", () => { Save(); return Task.CompletedTask; }),
                UiAction("Load example", () => { editor.Text = "# Open Settings\napp com.android.settings\nwait 800\nkey HOME"; return Task.CompletedTask; })),
            status, UiText("Double-click a saved macro to open it.", heading: true), saved,
            UiRow(UiAction("Run on connection", () =>
            {
                if (saved.SelectedItem is not string label) throw new InvalidOperationException("Choose a saved macro.");
                if (Confirm($"Run “{label}” automatically on newly authorized devices, including at EzAdb startup?", "Macro autorun"))
                {
                    SaveSetting(AutomaticMacroFile, label);
                    Reload();
                }
                return Task.CompletedTask;
            }), UiAction("Disable autorun", () => { SaveSetting(AutomaticMacroFile, ""); Reload(); return Task.CompletedTask; })), automatic);
        var back = UiAction("Back to Screen", () => { ScreenNav.IsChecked = true; return Task.CompletedTask; });
        return new DevicePage(UiStack(back, view), "Macro library",
            "Manage macros and autorun. Open macro controls for active sessions from Screen.", _ => Reload());
    }

    private void RunConnectionMacro(Device[] previous)
    {
        if (ReadSetting(AutomaticMacroFile) is not { Length: > 0 } label) return;
        var alreadyReady = previous.Where(device => device.Ready).Select(device => (device.Serial, device.TransportId)).ToHashSet();
        foreach (var device in knownDevices.Where(device => device.Ready &&
            !alreadyReady.Contains((device.Serial, device.TransportId))))
        {
            if (!automaticRuns.Add(device.Serial)) continue;
            _ = RunPageTask("Macro autorun", async () =>
            {
                try
                {
                    var steps = ParseMacro(File.ReadAllText(LocalChild(MacroDirectory, label + ".txt")));
                    Log($"Autorun: {label} on {device.DisplayName}");
                    await RunMacroAsync(device, steps, lifetime.Token, Log);
                }
                finally { automaticRuns.Remove(device.Serial); }
            });
        }
    }

    private DevicePage BuildBatchPage()
    {
        batchDevices = UiList("Android devices for the batch action", 156, multiple: true, nameof(Device.ConnectedName));
        var remote = UiField("Android path", "/sdcard/Download");
        var command = UiField("ADB command", "shell getprop ro.product.model");
        var output = UiEditor("Batch output", 195, readOnly: true);
        Device[] Pick() => batchDevices.SelectedItems.Cast<Device>().ToArray() is { Length: > 0 } selected
            ? selected : throw new InvalidOperationException("Select at least one authorized device.");

        async Task Execute(Device[] devices, Func<Device, Task> action)
        {
            await Task.WhenAll(devices.Select(async device =>
            {
                try
                {
                    await action(device);
                    AppendOutput(output, $"[{device.DisplayName} / {device.Serial}] Done.");
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception error) { AppendOutput(output, $"[{device.DisplayName} / {device.Serial}] {error.Message}"); }
            }));
        }

        var view = UiCard(batchDevices, UiRow(UiAction("Select all", () =>
                { batchDevices.SelectAll(); return Task.CompletedTask; }),
            UiAction("Open screen windows", () =>
            {
                foreach (var device in Pick()) _ = MirrorDeviceAsync(device);
                return Task.CompletedTask;
            }, true),
            UiAction("Install APKs", async () =>
            {
                var devices = Pick();
                var dialog = new OpenFileDialog { Title = "APKs for all selected devices", Filter = "Android APKs (*.apk)|*.apk", Multiselect = true };
                if (dialog.ShowDialog(this) != true) return;
                await Execute(devices, async device =>
                {
                    foreach (var file in dialog.FileNames)
                    {
                        var result = await DeviceCommand(device, ["install", "-r", file], seconds: 300);
                        result.RequireSuccess(result => result.Split('\n', StringSplitOptions.TrimEntries).Contains("Success"));
                    }
                });
            })),
            Labeled("Android path for all selected devices", remote),
            UiRow(UiAction("Upload files", async () =>
                {
                    var devices = Pick();
                    var path = PathOnDevice(remote.Text);
                    var dialog = new OpenFileDialog { Title = "Select files to upload", Multiselect = true };
                    if (dialog.ShowDialog(this) != true) return;
                    await Execute(devices, device => PushPathsAsync(device, path, dialog.FileNames, askOverwrite: false));
                }),
                UiAction("Download file or folder", async () =>
                {
                    var devices = Pick();
                    var path = PathOnDevice(remote.Text);
                    if (BrowseDirectory("Choose a Windows destination folder") is not { } folder) return;
                    await Execute(devices, async device =>
                    {
                        var target = Path.Combine(folder, $"{SafeFolderName(device.Serial)}-{Guid.NewGuid().ToString("N")[..6]}");
                        Directory.CreateDirectory(target);
                        await PullPathsAsync(device, [path], target, askOverwrite: false);
                    });
                })),
            Labeled("ADB command for all selected devices", command),
            UiAction("Run on selected devices", async () =>
            {
                var arguments = ParseAdbArguments(command.Text, deviceOnly: true);
                await Execute(Pick(), async device =>
                {
                    var progress = new Progress<string>(line => AppendOutput(output, $"[{device.Serial}] {line}"));
                    (await DeviceCommand(device, arguments, seconds: 60, output: progress)).RequireSuccess();
                });
            }, true), output);
        UpdateBatchDevices();
        return new DevicePage(view, "Multiple devices", "Use Ctrl or Shift to select devices and run actions in parallel.");
    }

    private void UpdateBatchDevices()
    {
        if (batchDevices is null) return;
        var selected = batchDevices.SelectedItems.Cast<Device>().Select(device => device.Serial).ToHashSet(StringComparer.Ordinal);
        var devices = knownDevices.Where(device => device.Ready).ToArray();
        batchDevices.ItemsSource = devices;
        foreach (var device in devices.Where(device => selected.Contains(device.Serial)))
            batchDevices.SelectedItems.Add(device);
    }

    private static string SafeFolderName(string value) => "Android-" + string.Concat(value.Select(c =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
