using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static EzAdb.DeviceCommands;

namespace EzAdb;

internal sealed partial class Dashboard
{
    private const string PointHeader = "# EzAdb-Klickpunkte ";

    internal sealed record ClickPoint(int X, int Y, int Delay, int Hold, int Repeats)
    {
        public string Line => FormattableString.Invariant($"point {X} {Y} {Delay} {Hold} {Repeats}");

        public static ClickPoint Parse(string value, Size? display)
        {
            var fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var n = new int[5];
            if (fields.Length != n.Length) throw new ArgumentException("Ein Klickpunkt benötigt X, Y, Wartezeit, Haltedauer und Wiederholungen.");
            for (int i = 0; i < n.Length; i++)
                if (!int.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i]))
                    throw new ArgumentException("Die Werte eines Klickpunkts müssen nichtnegative Ganzzahlen sein.");
            if (n[2] > 600000 || n[3] > 30000 || n[4] is < 1 or > 1000 ||
                display is { } size && (n[0] >= size.Width || n[1] >= size.Height))
                throw new ArgumentException("Klickpunkt ungültig. Prüfe Position, Wartezeit, Haltedauer und Wiederholungen.");
            return new(n[0], n[1], n[2], n[3], n[4]);
        }

        public MacroStep Step(int? display, Size? size)
        {
            var gesture = Hold == 0 ? $"tap {X} {Y}" : $"swipe {X} {Y} {X} {Y} {Hold}";
            return new(Shell($"{InputPrefix(display)} {gesture}"), Delay, Repeats, size, display);
        }
    }

    private static Size? PointMacroSize(string header)
    {
        if (!header.StartsWith(PointHeader, StringComparison.Ordinal)) return null;
        return ParsePointSize(header[PointHeader.Length..].Trim());
    }

    private static Size ParsePointSize(string value)
    {
        var dimensions = value.Split('x');
        if (dimensions.Length == 2 && int.TryParse(dimensions[0], out var w) && int.TryParse(dimensions[1], out var h) && w > 0 && h > 0)
            return new Size(w, h);
        throw new ArgumentException("Die Displaygröße ist ungültig.");
    }

    internal static Size ParsePointDisplay(string output, int display)
    {
        bool target = false;
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            var header = Regex.Match(line, @"^Display:\s*mDisplayId=(\d+)\b");
            if (header.Success) target = header.Groups[1].Value == display.ToString(CultureInfo.InvariantCulture);
            if (target && Regex.Match(line, @"\bcur=(\d+x\d+)\b") is { Success: true } match)
                return ParsePointSize(match.Groups[1].Value);
        }
        throw new InvalidOperationException($"Android meldet keine Bildschirmgröße für Display {display}.");
    }

    private async Task<Size> ReadPointDisplay(Device device, int? display, CancellationToken cancellation) =>
        ParsePointDisplay(await AndroidShell(device, "dumpsys window displays", cancellation), display ?? 0);

    internal static Rect VideoBounds(Size client, Size android)
    {
        var scale = Math.Min(client.Width / android.Width, client.Height / android.Height);
        var w = android.Width * scale;
        var h = android.Height * scale;
        return new Rect((client.Width - w) / 2, (client.Height - h) / 2, w, h);
    }

    internal static Point? AndroidPoint(Point click, Rect image, Size android)
    {
        if (image.Width <= 0 || image.Height <= 0 || !image.Contains(click) || click.X >= image.Right || click.Y >= image.Bottom) return null;
        return new Point(Math.Min(android.Width - 1, Math.Floor((click.X - image.X) * android.Width / image.Width)),
            Math.Min(android.Height - 1, Math.Floor((click.Y - image.Y) * android.Height / image.Height)));
    }

    private FrameworkElement BuildClickPointEditor(Window window, Size display, string? name, List<ClickPoint> points,
        Action<string?> finish)
    {
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/EzAdb;component/Theme.xaml", UriKind.Relative) });
        var surface = new Grid { Background = new SolidColorBrush(Color.FromArgb(8, 0, 0, 0)), Focusable = true, ClipToBounds = true };
        var canvas = new Canvas { Background = Brushes.Transparent, Cursor = Cursors.Cross, ClipToBounds = true };
        var bar = new Border { Background = Paint("Surface"), BorderBrush = Paint("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Margin = new Thickness(5),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var hint = UiText("Klicke ins Bild, um Punkt 1 zu setzen.");
        hint.Margin = new Thickness(0, 0, 0, 4);
        int moving = -1;
        bool dirty = false;

        void Draw()
        {
            canvas.Children.Clear();
            var image = VideoBounds(canvas.RenderSize, display);
            for (var i = 0; i < points.Count; i++)
            {
                var index = i;
                var point = points[i];
                var marker = new Button { Content = (i + 1).ToString(), Width = 34, Height = 34, MinHeight = 0,
                    Padding = default, Style = (Style)FindResource("Primary"), ToolTip = $"Punkt {i + 1} bearbeiten · {point.X}, {point.Y}" };
                Canvas.SetLeft(marker, image.Left + (point.X + 0.5) * image.Width / display.Width - 17);
                Canvas.SetTop(marker, image.Top + (point.Y + 0.5) * image.Height / display.Height - 17);
                marker.Click += (_, _) => { if (moving < 0) Edit(index, point); };
                canvas.Children.Add(marker);
            }
        }
        void Hint() => hint.Text = moving >= 0 ? $"Neue Position für Punkt {moving + 1} anklicken." :
            $"Punkt {points.Count + 1} im Bild setzen · Nummer anklicken zum Bearbeiten";

        void Edit(int index, ClickPoint point)
        {
            var exists = index < points.Count;
            var delay = UiField("Wartezeit", point.Delay.ToString(), 105);
            var hold = UiField("Haltedauer", point.Hold.ToString(), 105);
            var repeats = UiField("Wiederholungen", point.Repeats.ToString(), 105);
            var order = UiField("Reihenfolge", (index + 1).ToString(), 105);
            var feedback = UiText("");
            var fields = UiStack(UiText($"Position {point.X}, {point.Y}"),
                UiRow(Labeled("Wartezeit (ms)", delay), Labeled("Haltedauer (ms)", hold)),
                UiRow(Labeled("Wiederholungen", repeats), Labeled("Reihenfolge", order)),
                UiText("Die Wartezeit gilt vor jedem Druck. 0 ms Haltedauer entspricht einem kurzen Tippen."), feedback);
            var dialog = CreateToolDialog(window, $"Klickpunkt {index + 1}", 480, fields);
            var apply = UiAction("Übernehmen", () =>
            {
                try
                {
                    int position = ReadNumber(order, "Reihenfolge", 1, points.Count + (exists ? 0 : 1)) - 1;
                    var updated = point with { Delay = ReadNumber(delay, "Wartezeit", 0, 600000),
                        Hold = ReadNumber(hold, "Haltedauer", 0, 30000), Repeats = ReadNumber(repeats, "Wiederholungen", 1, 1000) };
                    if (exists) points.RemoveAt(index);
                    points.Insert(position, updated);
                    dirty = true;
                    dialog.DialogResult = true;
                }
                catch (ArgumentException error) { feedback.Text = error.Message; }
                return Task.CompletedTask;
            }, true);
            apply.IsDefault = true;
            var cancel = new Button { Content = "Abbrechen", IsCancel = true, Style = (Style)FindResource("Action") };
            var actions = UiRow(apply, cancel);
            if (exists)
            {
                actions.Children.Add(UiAction("Versetzen", () => { moving = index; dialog.DialogResult = true; return Task.CompletedTask; }));
                actions.Children.Add(UiAction("Entfernen", () => { points.RemoveAt(index); dirty = true; dialog.DialogResult = true; return Task.CompletedTask; }));
            }
            fields.Children.Add(actions);
            dialog.ShowDialog();
            Draw();
            Hint();
        }
        canvas.MouseLeftButtonDown += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, canvas)) return;
            e.Handled = true;
            if (AndroidPoint(e.GetPosition(canvas), VideoBounds(canvas.RenderSize, display), display) is not { } pixel)
            { hint.Text = "Klicke innerhalb des Android-Bilds."; return; }
            if (moving >= 0)
            {
                points[moving] = points[moving] with { X = (int)pixel.X, Y = (int)pixel.Y };
                moving = -1;
                dirty = true;
                Draw();
                Hint();
            }
            else Edit(points.Count, new ClickPoint((int)pixel.X, (int)pixel.Y, 250, 0, 1));
        };
        void Leave()
        {
            if (!dirty || MessageBox.Show(window, "Änderungen an den Klickpunkten verwerfen?", "Punkteditor schließen",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes) finish(null);
        }
        var save = UiAction("Speichern", () =>
        {
            if (points.Count == 0) { hint.Text = "Setze zuerst einen Klickpunkt."; return Task.CompletedTask; }
            var field = UiField("Makroname", name ?? "");
            var ok = new Button { Content = "Makro speichern", IsDefault = true, Style = (Style)FindResource("Primary") };
            var errorText = UiText("");
            var dialog = CreateToolDialog(window, "Klickfolge speichern", 370, UiStack(Labeled("Makroname", field), errorText,
                UiRow(ok, new Button { Content = "Abbrechen", IsCancel = true, Style = (Style)FindResource("Action") })));
            ok.Click += (_, _) =>
            {
                try
                {
                    var label = field.Text.Trim();
                    if (label.Length == 0) throw new ArgumentException("Gib dem Makro einen Namen.");
                    if (!string.Equals(label, name, StringComparison.OrdinalIgnoreCase) && File.Exists(MacroPath(label)) &&
                        MessageBox.Show(dialog, $"Makro „{label}“ überschreiben?", "Klickfolge speichern", MessageBoxButton.YesNo,
                            MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                    SaveMacro(label, FormattableString.Invariant($"{PointHeader}{display.Width}x{display.Height}\n") +
                        string.Join('\n', points.Select(point => point.Line)));
                    dialog.DialogResult = true;
                }
                catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { errorText.Text = error.Message; }
            };
            dialog.Loaded += (_, _) => { field.Focus(); field.SelectAll(); };
            if (dialog.ShowDialog() == true) finish(field.Text.Trim());
            return Task.CompletedTask;
        }, true);
        var shift = UiAction("Leiste versetzen", () =>
        {
            bar.VerticalAlignment = bar.VerticalAlignment == VerticalAlignment.Top ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            return Task.CompletedTask;
        });
        bar.Child = UiStack(hint, UiRow(save, shift, UiAction("Zurück", () => { Leave(); return Task.CompletedTask; })));
        surface.Children.Add(canvas);
        surface.Children.Add(bar);
        canvas.SizeChanged += (_, _) => Draw();
        surface.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (moving >= 0) { moving = -1; Hint(); } else Leave();
        };
        Hint();
        window.Loaded += (_, _) => surface.Focus();
        return surface;
    }
}
