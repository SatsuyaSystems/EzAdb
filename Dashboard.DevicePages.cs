using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace EzAdb;

internal sealed partial class Dashboard
{
    private sealed record DevicePage(FrameworkElement View, string Title, string Description, Action<Device?>? DeviceChanged = null);

    private readonly Dictionary<string, DevicePage> devicePages = new();
    private readonly bool allowDevicePolling;
    private string? activeDevicePage;
    private Device? lastPageDevice;
    private string LocalData => Path.GetDirectoryName(settingsFile)!;

    private void InitializeDevicePages()
    {
        Closing += OnStudioClosing;
        Closed += (_, _) => StopDevicePageWork();
    }

    private (string Title, string Description)? ShowDevicePage(string key)
    {
        if (key != nameof(HealthNav))
        {
            StopHealthPolling();
        }
        if (!devicePages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                nameof(FilesNav) => BuildFilesPage(),
                nameof(AppsNav) => BuildAppsPage(),
                nameof(HealthNav) => BuildHealthPage(),
                nameof(MediaNav) => BuildMediaPage(),
                nameof(RemoteNav) => BuildRemotePage(),
                nameof(ConsoleNav) => BuildConsolePage(),
                nameof(MacrosNav) => BuildMacrosPage(),
                nameof(BatchNav) => BuildBatchPage(),
                nameof(BackupNav) => BuildBackupPage(),
                _ => null
            };
            if (page is not null)
            {
                devicePages.Add(key, page);
            }
        }
        activeDevicePage = page is null ? null : key;
        DeviceToolPage.Content = page?.View;
        DeviceToolPage.Visibility = Visible(page is not null);
        lastPageDevice = CurrentReadyDevice;
        page?.DeviceChanged?.Invoke(lastPageDevice);
        return page is null ? null : (page.Title, page.Description);
    }

    private Device? CurrentReadyDevice => adbIssue is null && Selected is { Ready: true } device ? device : null;

    private Device RequireDevice() => CurrentReadyDevice ??
        throw new InvalidOperationException("Select a connected and authorized Android device in the upper-right corner.");

    private void RefreshDevicePageSelection()
    {
        var device = CurrentReadyDevice;
        if (!Equals(lastPageDevice, device) && activeDevicePage is { } key)
        {
            lastPageDevice = device;
            devicePages[key].DeviceChanged?.Invoke(device);
        }
    }

    private void DeviceListChanged(Device[] previous)
    {
        UpdateBatchDevices();
        RememberConnectedWifi(previous);
        if (allowDevicePolling)
        {
            RunConnectionMacro(previous);
        }
    }

    private void StopDevicePageWork()
    {
        StopHealthPolling();
        DisposeDragCache();
    }

    private async Task RunPageTask(string label, Func<Task> run, Control? action = null)
    {
        if (lifetime.IsCancellationRequested || action is { IsEnabled: false })
        {
            return;
        }
        if (action is not null)
        {
            action.IsEnabled = false;
        }
        try { await run(); }
        catch (OperationCanceledException) { }
        catch (ToolDependencyException error) { ReportDependency(error); }
        catch (Exception error) { ReportFailure(label, error); }
        finally
        {
            if (action is not null && !lifetime.IsCancellationRequested)
            {
                action.IsEnabled = true;
            }
        }
    }

    private Task<CommandResult> DeviceCommand(Device device, string[] arguments, CancellationToken cancellation = default,
        int seconds = 20, IProgress<string>? output = null) =>
        tools.RunDeviceAsync(device, arguments, cancellation.CanBeCanceled ? cancellation : lifetime.Token, seconds, progress: output);

    private async Task<string> AndroidShell(Device device, string command, CancellationToken cancellation = default, int seconds = 20)
    {
        var result = await DeviceCommand(device, DeviceCommands.Shell(command), cancellation, seconds);
        DeviceCommands.CheckAndroid(result);
        return result.Output;
    }

    private TextBlock UiText(string text, bool heading = false) => new()
    {
        Text = text, Style = (Style)FindResource(heading ? "SectionTitle" : "MutedText"),
        Margin = new Thickness(0, 0, 0, heading ? 7 : 11)
    };

    private Button UiAction(string label, Func<Task> action, bool primary = false)
    {
        var button = new Button
        {
            Content = label, Style = (Style)FindResource(primary ? "Primary" : "Action"),
            Margin = new Thickness(0, 0, 8, 7)
        };
        button.Click += async (_, _) => await RunPageTask(label, action, button);
        return button;
    }

    private static WrapPanel UiRow(params UIElement[] controls)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 1, 0, 3) };
        foreach (var control in controls)
        {
            row.Children.Add(control);
        }
        return row;
    }

    private Border UiCard(params UIElement[] content) => new()
    {
        Style = (Style)FindResource("Tile"), Padding = new Thickness(19), Margin = new Thickness(0, 0, 0, 12),
        Child = UiStack(content)
    };

    private static StackPanel UiStack(params UIElement[] controls)
    {
        var panel = new StackPanel();
        foreach (var control in controls)
        {
            panel.Children.Add(control);
        }
        return panel;
    }

    private TextBox UiField(string label, string value = "", double width = double.NaN)
    {
        var field = new TextBox
        {
            Text = value, Width = width, Style = (Style)FindResource("Field"), CaretBrush = Paint("Ink"),
            Margin = new Thickness(0, 0, 8, 8), MinWidth = 90
        };
        AutomationProperties.SetName(field, label);
        return field;
    }

    private TextBox UiEditor(string label, int height, bool readOnly = false)
    {
        var editor = new TextBox
        {
            MinHeight = height, Height = height, AcceptsReturn = !readOnly, IsReadOnly = readOnly, IsUndoEnabled = !readOnly,
            Background = Paint("Canvas"), Foreground = Paint("Ink"), BorderBrush = Paint("Line"), CaretBrush = Paint("Ink"),
            BorderThickness = new Thickness(1), Padding = new Thickness(10), FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 8)
        };
        AutomationProperties.SetName(editor, label);
        return editor;
    }

    private FrameworkElement Labeled(string label, Control field)
    {
        var caption = new Label
        {
            Content = label, Target = field, Foreground = Paint("Muted"), Padding = new Thickness(0, 3, 0, 4)
        };
        return UiStack(caption, field);
    }

    private ListBox UiList(string label, int height, bool multiple = false, string? display = null)
    {
        var list = new ListBox
        {
            Height = height, Background = Paint("Canvas"), Foreground = Paint("Ink"), BorderBrush = Paint("Line"),
            Padding = new Thickness(4), Margin = new Thickness(0, 0, 0, 9),
            SelectionMode = multiple ? SelectionMode.Extended : SelectionMode.Single,
            DisplayMemberPath = display ?? ""
        };
        AutomationProperties.SetName(list, label);
        return list;
    }

    private CheckBox UiCheck(string label, bool enabled = false) => new()
    {
        Content = label, Foreground = Paint("Ink"), IsChecked = enabled, Margin = new Thickness(0, 4, 14, 8)
    };

    private static void AppendOutput(TextBox destination, string line)
    {
        if (destination.Text.Length > 120_000)
        {
            destination.Text = destination.Text[^90_000..];
        }
        destination.AppendText(line + Environment.NewLine);
        destination.ScrollToEnd();
    }

    private string? AskText(string title, string initial = "")
    {
        var field = UiField(title, initial);
        var ok = new Button { Content = "Apply", IsDefault = true, Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = (Style)FindResource("Action") };
        var window = new Window
        {
            Owner = this, Title = title, Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Paint("Surface"), Foreground = Paint("Ink"),
            Content = new Border { Padding = new Thickness(19), Child = UiStack(Labeled(title, field), UiRow(ok, cancel)) }
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { field.Focus(); field.SelectAll(); };
        return window.ShowDialog() == true ? field.Text : null;
    }

    private bool Confirm(string question, string title) => MessageBox.Show(this, question, title, MessageBoxButton.YesNo,
        MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private string? BrowseDirectory(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private static int ReadNumber(TextBox field, string name, int min, int max)
    {
        if (!int.TryParse(field.Text, out var value) || value < min || value > max)
        {
            throw new ArgumentException($"{name} must be between {min} and {max} are listed below.");
        }
        return value;
    }
}
