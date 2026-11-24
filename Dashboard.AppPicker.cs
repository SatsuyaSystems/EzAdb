using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EzAdb;

internal sealed partial class Dashboard
{
    internal sealed record LaunchableApp(string Name, string Package);

    internal static LaunchableApp[] FilterLaunchableApps(IEnumerable<LaunchableApp> apps, string query)
    {
        query = query.Trim();
        return apps.Where(app => app.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                          app.Package.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(app => app.Package, StringComparer.Ordinal).ToArray();
    }

    internal LaunchableApp? PickAndroidApp(Device device, Window owner,
        Func<CancellationToken, Task<LaunchableApp[]>>? readApps = null)
    {
        using var loading = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = loading.Token;
        var search = UiField("Search by app name");
        var list = UiList("Launchable Android apps", 285);
        list.ItemTemplate = (DataTemplate)FindResource("AppPickerRow");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var status = UiText("Loading launchable apps…");
        var open = new Button { Content = "Choose app", IsDefault = true, IsEnabled = false, Style = (Style)FindResource("Primary") };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = (Style)FindResource("Action"),
            Margin = new Thickness(8, 0, 0, 0) };
        var dialog = CreateToolDialog(owner, "Select an Android app", 510,
            UiStack(UiText(device.ConnectedName), Labeled("Search by app name or package ID", search), list, status, UiRow(open, cancel)));
        LaunchableApp[] apps = [];

        void Filter()
        {
            var filtered = FilterLaunchableApps(apps, search.Text);
            list.ItemsSource = filtered;
            list.SelectedIndex = filtered.Length > 0 ? 0 : -1;
            status.Text = apps.Length == 0 ? "No launchable apps found." :
                filtered.Length == 0 ? "No matching apps found." : $"{filtered.Length} of {apps.Length} Apps";
        }
        search.TextChanged += (_, _) => { if (apps.Length > 0) Filter(); };
        list.SelectionChanged += (_, _) => open.IsEnabled = list.SelectedItem is LaunchableApp;
        open.Click += (_, _) => dialog.DialogResult = true;
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is LaunchableApp) dialog.DialogResult = true; };
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && list.SelectedItem is { } selected)
            {
                list.ScrollIntoView(selected);
                if (list.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item)
                {
                    item.Focus();
                    e.Handled = true;
                }
            }
        };
        dialog.Loaded += async (_, _) =>
        {
            search.Focus();
            try
            {
                apps = await (readApps?.Invoke(token) ?? ReadLaunchableAppsAsync(device, token));
                if (!token.IsCancellationRequested) Filter();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { status.Text = $"Could not load apps. {error.Message}"; }
        };
        dialog.Closed += (_, _) => loading.Cancel();
        return dialog.ShowDialog() == true ? list.SelectedItem as LaunchableApp : null;
    }

    private async Task<LaunchableApp[]> ReadLaunchableAppsAsync(Device device, CancellationToken cancellation)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        probe.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var result = await tools.RunScrcpyAsync(device, "EzAdb App Picker", ["--list-apps"], probe.Token);
            if (result.ExitCode == 0 && DeviceCommands.ParseAppNames(result.Details) is { Count: > 0 } names)
            {
                return names.Select(pair => new LaunchableApp(pair.Value, pair.Key)).ToArray();
            }
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested &&
            error is OperationCanceledException or ToolDependencyException or InvalidOperationException) { }

        var activities = await AndroidShell(device,
            "cmd package query-activities --brief --user current -a android.intent.action.MAIN -c android.intent.category.LAUNCHER", cancellation);
        return activities.Split('\n', StringSplitOptions.TrimEntries)
            .Select(line => line.Split('/', 2)).Where(parts => parts.Length == 2 && DeviceCommands.IsPackage(parts[0]))
            .Select(parts => parts[0]).Distinct(StringComparer.Ordinal).Select(package => new LaunchableApp(package, package)).ToArray();
    }

    private Window CreateToolDialog(Window owner, string title, int width, FrameworkElement content) => new()
    {
        Owner = owner, Title = title, Width = width, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Paint("Surface"), Foreground = Paint("Ink"),
        FontFamily = FontFamily, FontSize = FontSize, ShowInTaskbar = false,
        Resources = new ResourceDictionary { MergedDictionaries = { new ResourceDictionary { Source = new Uri("/EzAdb;component/Theme.xaml", UriKind.Relative) } } },
        MaxHeight = SystemParameters.WorkArea.Height,
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Padding = new Thickness(20), Child = content } }
    };
}
