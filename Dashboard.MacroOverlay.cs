using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using static EzAdb.DeviceCommands;

namespace EzAdb;

internal sealed partial class Dashboard
{
    internal sealed record MacroSession(Process Process, Device Device, CancellationToken Token, Func<int?>? VirtualDisplay,
        Func<int?, Task<Size>>? ReadSize)
    {
        public Window? Controls { get; set; }
        public override string ToString() => VirtualDisplay is null ? $"{Device.ConnectedName} · Live-Steuerung" :
            $"{Device.ConnectedName} · Virtuelles Display {VirtualDisplay()?.ToString() ?? "(startet)"}";
    }

    private readonly List<MacroSession> macroSessions = [];

    internal MacroSession TrackMacroSession(Process process, Device device, CancellationToken token, Func<int?>? virtualDisplay = null,
        Func<int?, Task<Size>>? readSize = null)
    {
        var session = new MacroSession(process, device, token, virtualDisplay, readSize);
        macroSessions.Add(session);
        return session;
    }

    internal void UntrackMacroSession(MacroSession? session)
    {
        if (session is null) return;
        macroSessions.Remove(session);
        session.Controls?.Close();
    }

    private void OnOpenMacroControls(object sender, RoutedEventArgs e)
    {
        var active = macroSessions.Where(session => !session.Token.IsCancellationRequested).ToArray();
        if (active.Length == 0)
        {
            Notify("Starte zuerst die Live-Steuerung oder ein virtuelles Display.", true);
            return;
        }
        if (active is [var session])
        {
            ShowMacroControls(session);
            return;
        }

        var list = UiList("Aktive Bildschirmfenster", 135);
        list.ItemsSource = active;
        list.SelectedIndex = Math.Max(0, Array.FindIndex(active, item => item.Device.Serial == Selected?.Serial));
        var open = new Button { Content = "Makro-Steuerung öffnen", IsDefault = true, Style = (Style)FindResource("Primary") };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, Style = (Style)FindResource("Action") };
        var dialog = CreateToolDialog(this, "Bildschirmfenster auswählen", 495, UiStack(list, UiRow(open, cancel)));
        open.Click += (_, _) => { if (list.SelectedItem is MacroSession) dialog.DialogResult = true; };
        if (dialog.ShowDialog() == true && list.SelectedItem is MacroSession picked) ShowMacroControls(picked);
    }

    private void ShowMacroControls(MacroSession targetSession)
    {
        if (!macroSessions.Contains(targetSession) || targetSession.Token.IsCancellationRequested) return;
        if (targetSession.Controls is { } existing)
        {
            if (existing.IsVisible)
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
            }
            return;
        }

        var content = UiStack();
        var window = CreateToolDialog(this, "EzAdb · Makro-Steuerung", 425, content);
        var device = targetSession.Device;
        var session = targetSession.Token;
        var virtualDisplay = targetSession.VirtualDisplay;
        var target = UiText(targetSession.ToString());
        var status = UiText("");
        var saved = UiList("Gespeicherte Makros", 96);
        CancellationTokenSource? running = null;
        bool editingPoints = false;
        bool closed = false;
        ScrcpyOverlay? pointOverlay = null;

        int? Display() => virtualDisplay is null ? null : virtualDisplay() ??
            throw new InvalidOperationException("Das virtuelle Display wird noch initialisiert.");

        Button RunHere(string label, Func<Task> action, bool primary = false) => UiAction(label, async () =>
        {
            try { await action(); }
            catch (OperationCanceledException) when (session.IsCancellationRequested) { }
            catch (Exception error)
            {
                status.Text = error.Message;
                Log($"{label}: {error.Message}");
            }
        }, primary);

        var stop = RunHere("Stoppen", () => { running?.Cancel(); return Task.CompletedTask; });
        stop.IsEnabled = false;
        var run = RunHere("Ausführen", async () =>
        {
            if (running is not null || editingPoints) return;
            if (saved.SelectedItem is not string name) throw new InvalidOperationException("Wähle zuerst ein Makro aus.");
            var steps = ParseMacro(File.ReadAllText(MacroPath(name)), Display());
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(session);
            running = cancellation;
            stop.IsEnabled = true;
            try
            {
                await RunMacroAsync(device, steps, cancellation.Token, line => status.Text = line);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                status.Text = "Makro gestoppt.";
            }
            finally { running = null; stop.IsEnabled = false; }
        }, true);
        window.Closed += (_, _) =>
        {
            closed = true;
            targetSession.Controls = null;
            running?.Cancel();
            pointOverlay?.Dispose();
        };

        var library = UiStack(saved);
        var edit = RunHere("Bearbeiten", async () =>
        {
            if (saved.SelectedItem is not string name) throw new InvalidOperationException("Wähle ein Makro aus.");
            var lines = File.ReadAllLines(MacroPath(name));
            if (lines.Length > 0 && PointMacroSize(lines[0]) is { } size &&
                lines.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)).All(line => line.StartsWith("point ", StringComparison.Ordinal)))
                await OpenPoints(name, size, lines.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(line => ClickPoint.Parse(line[6..], size)).ToList());
            else if (EditOverlayMacro(window, device, name) is { } updated) Reload(updated);
        });
        var newMacro = RunHere("Neues Makro", () =>
        {
            if (EditOverlayMacro(window, device, null) is { } name) Reload(name);
            return Task.CompletedTask;
        });
        library.Children.Add(UiRow(run, stop, edit));
        var pointEditor = RunHere("Klickpunkte setzen", () => OpenPoints(null, null, []), true);
        content.Children.Add(target);
        content.Children.Add(UiRow(pointEditor, newMacro));
        content.Children.Add(library);
        content.Children.Add(status);
        async Task OpenPoints(string? name, Size? expected, List<ClickPoint> points)
        {
            if (editingPoints) return;
            if (running is not null) throw new InvalidOperationException("Stoppe zuerst das laufende Makro.");
            editingPoints = true;
            try
            {
                var display = Display();
                var size = await (targetSession.ReadSize?.Invoke(display) ?? ReadPointDisplay(device, display, session));
                if (closed || session.IsCancellationRequested) return;
                if (expected.HasValue && expected != size)
                    throw new InvalidOperationException($"Diese Klickfolge wurde für {expected.Value.Width} × {expected.Value.Height} angelegt.");
                var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                pointOverlay = new ScrcpyOverlay(targetSession.Process, editor => BuildClickPointEditor(editor, size, name, points, selected =>
                {
                    result.TrySetResult(selected);
                    editor.Close();
                }));
                pointOverlay.Window.Closed += (_, _) => result.TrySetResult(null);
                window.Hide();
                pointOverlay.Window.Activate();
                var selected = await result.Task;
                pointOverlay = null;
                if (!closed && !session.IsCancellationRequested)
                {
                    window.Show();
                    Reload(selected ?? name);
                }
            }
            finally { editingPoints = false; }
        }
        void Reload(string? name = null)
        {
            var selected = name ?? saved.SelectedItem as string;
            var names = SavedMacroNames();
            saved.ItemsSource = names;
            saved.SelectedItem = selected;
            if (saved.SelectedIndex < 0 && names.Length > 0) saved.SelectedIndex = 0;
            var hasMacros = names.Length > 0;
            library.Visibility = Visible(hasMacros);
            if (!hasMacros) status.Text = "Noch keine gespeicherten Makros.";
            else if (running is null) status.Text = "";
            target.Text = targetSession.ToString();
        }
        if (virtualDisplay is not null)
        {
            content.Children.Add(RunHere("App auf diesem Display öffnen", async () =>
            {
                var id = Display()!.Value;
                if (PickAndroidApp(device, window) is { } app)
                {
                    session.ThrowIfCancellationRequested();
                    await AndroidShell(device, Launch(app.Package, id), session);
                    status.Text = $"{app.Name} geöffnet.";
                }
            }));
        }
        try { Reload(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { status.Text = error.Message; }
        targetSession.Controls = window;
        window.Show();
    }

    private string? EditOverlayMacro(Window owner, Device device, string? existingName)
    {
        var name = UiField("Makroname", existingName ?? "");
        var editor = UiEditor("Ablauf", 167);
        if (existingName is not null) editor.Text = File.ReadAllText(MacroPath(existingName));
        var status = UiText("");
        var pendingLabel = UiText("");
        var pendingValue = UiField("Wert für den Makroschritt");
        string? pendingVerb = null;
        var presets = new WrapPanel();
        var save = new Button { Content = "Makro speichern", Style = (Style)FindResource("Primary") };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, Style = (Style)FindResource("Action"),
            Margin = new Thickness(8, 0, 0, 0) };
        var add = new Button { Content = "Schritt hinzufügen", Style = (Style)FindResource("Action") };
        var pending = UiStack(pendingLabel, pendingValue, add);
        pending.Margin = new Thickness(0, 0, 0, 12);
        pending.Visibility = Visibility.Collapsed;
        var dialog = CreateToolDialog(owner, existingName is null ? "Neues Makro" : $"Makro bearbeiten · {existingName}", 530,
            UiStack(Labeled("Makroname", name), UiText("Schritt hinzufügen", true), presets, pending, editor, status, UiRow(save, cancel)));

        void AddStep(string line)
        {
            _ = ParseMacro(line);
            editor.AppendText((editor.Text.Length == 0 || editor.Text.EndsWith('\n') ? "" : Environment.NewLine) +
                line + Environment.NewLine);
            editor.ScrollToEnd();
            pending.Visibility = Visibility.Collapsed;
            status.Text = "";
        }
        foreach (var (label, verb, instructions, example) in new[]
        {
            ("Tippen", "tap", "Koordinaten x y", "320 640"), ("Wischen", "swipe", "Start x y, Ziel x y, Dauer in ms", "100 800 100 250 400"),
            ("Text", "text", "Text für Android", ""), ("Warten", "wait", "Wartezeit in Millisekunden", "1000"),
            ("Taste", "key", "Android-Taste wie HOME oder BACK", "BACK"), ("ADB", "adb", "Gerätebezogener ADB-Befehl", "shell getprop ro.product.model")
        })
        {
            presets.Children.Add(UiAction(label, () =>
            {
                pendingVerb = verb;
                pendingLabel.Text = instructions;
                pendingValue.Text = example;
                pending.Visibility = Visibility.Visible;
                pendingValue.Focus();
                pendingValue.SelectAll();
                return Task.CompletedTask;
            }));
        }
        presets.Children.Add(UiAction("App starten", () =>
        {
            if (PickAndroidApp(device, dialog) is { } app) AddStep($"app {app.Package}");
            return Task.CompletedTask;
        }));
        add.Click += (_, _) =>
        {
            try { AddStep($"{pendingVerb} {pendingValue.Text}"); }
            catch (ArgumentException error) { status.Text = error.Message; }
        };
        save.Click += (_, _) =>
        {
            try
            {
                var label = name.Text.Trim();
                if (label.Length == 0) throw new ArgumentException("Gib dem Makro einen Namen.");
                if (!label.Equals(existingName, StringComparison.OrdinalIgnoreCase) && File.Exists(MacroPath(label)) &&
                    MessageBox.Show(dialog, $"Makro „{label}“ überschreiben?", "Makro speichern",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                SaveMacro(label, editor.Text);
                dialog.DialogResult = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { status.Text = error.Message; }
        };
        dialog.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        return dialog.ShowDialog() == true ? name.Text.Trim() : null;
    }
}
