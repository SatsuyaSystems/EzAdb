using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using static EzAdb.DeviceCommands;

namespace EzAdb;

internal sealed partial class Dashboard
{
    private string DragRoot => Path.Combine(LocalData, "Drag");
    private readonly Func<Device, string, Task<string>> readFiles;
    private readonly bool loadFilesOnNavigation;

    private DevicePage BuildFilesPage()
    {
        const string home = "/sdcard";
        var path = UiField("Android path", home);
        var search = UiField("Search this folder");
        var list = new ListView
        {
            Background = Paint("Canvas"), Foreground = Paint("Ink"), BorderBrush = Paint("Line"), Height = 325,
            SelectionMode = SelectionMode.Extended
        };
        System.Windows.Automation.AutomationProperties.SetName(list, "Files on Android");
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        var columns = new GridView { AllowsColumnReorder = false, ColumnHeaderContainerStyle = (Style)FindResource("FileColumnHeader") };
        var location = new GridViewColumn { Header = "Folder", Width = 205, DisplayMemberBinding = new Binding(nameof(RemoteEntry.Folder)) };
        var nameColumn = new GridViewColumn { Header = "Name", Width = 275, CellTemplate = (DataTemplate)FindResource("RemoteFileName") };
        columns.Columns.Add(nameColumn);
        columns.Columns.Add(new GridViewColumn { Header = "Type", Width = 110, DisplayMemberBinding = new Binding(nameof(RemoteEntry.Kind)) });
        list.View = columns;
        list.SizeChanged += (_, _) => nameColumn.Width = Math.Max(130, list.ActualWidth - (columns.Columns.Contains(location) ? 335 : 130));
        var crumbs = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
        var status = UiText("Choose an Android device.");
        var entries = Array.Empty<RemoteEntry>();
        var folderEntries = entries;
        RemoteEntry[] Selection() => list.SelectedItems.Cast<RemoteEntry>().ToArray();
        var history = new List<string>();
        int historyIndex = -1;
        bool searchResults = false;
        Device? openedDevice = null;
        var openedPath = home;
        string[] prepared = [];
        string[] preparedSources = [];
        var loading = 0;
        var dragStart = new Point();
        var dragging = false;

        var back = NavigationButton("←", "Back (Alt+Left)");
        var forward = NavigationButton("→", "Forward (Alt+Right)");
        var up = NavigationButton("↑", "Up one level (Alt+Up)");
        var refresh = NavigationButton("↻", "Refresh (F5)");
        back.Click += async (_, _) => await RunPageTask("Back", () => Load(RequireDevice(), history[historyIndex - 1], historyIndex - 1));
        forward.Click += async (_, _) => await RunPageTask("Forward", () => Load(RequireDevice(), history[historyIndex + 1], historyIndex + 1));
        up.Click += async (_, _) => await RunPageTask("Parent folder", () => Load(RequireDevice(), Parent(openedPath)));
        refresh.Click += async (_, _) => await RunPageTask("Refresh", () => Load(RequireDevice(), openedPath));

        Button NavigationButton(string label, string description)
        {
            var button = new Button { Content = label, Style = (Style)FindResource("Action"), MinWidth = 36, Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 0, 5, 5), ToolTip = description };
            System.Windows.Automation.AutomationProperties.SetName(button, description);
            return button;
        }

        void UpdateAddress()
        {
            path.Text = openedPath;
            back.IsEnabled = historyIndex > 0;
            forward.IsEnabled = historyIndex >= 0 && historyIndex < history.Count - 1;
            up.IsEnabled = openedDevice is not null && openedPath != "/";
            refresh.IsEnabled = CurrentReadyDevice is not null;
            crumbs.Children.Clear();
            AddCrumb("Android", "/");
            var parent = "";
            foreach (var segment in openedPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                parent += "/" + segment;
                AddCrumb(parent == home ? "Internal storage" : segment, parent);
            }

            void AddCrumb(string label, string directory)
            {
                var button = NavigationButton(label, directory);
                button.Click += async (_, _) => await RunPageTask("Open folder", () => Load(RequireDevice(), directory));
                button.Background = Brushes.Transparent;
                button.MinHeight = 29;
                button.MaxWidth = 170;
                button.Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis };
                button.IsEnabled = CurrentReadyDevice is not null;
                if (directory == openedPath) button.Foreground = Paint("Accent");
                if (crumbs.Children.Count > 0) crumbs.Children.Add(new TextBlock
                    { Text = "›", Foreground = Paint("Muted"), Margin = new Thickness(0, 4, 5, 0) });
                crumbs.Children.Add(button);
            }
        }

        void Filter()
        {
            var filter = search.Text.Trim();
            var visible = entries.Where(item => item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.ItemsSource = visible;
            status.Text = openedDevice is null ? "Choose an Android device or open a path." :
                searchResults ? $"{visible.Length} results in {openedPath}" :
                visible.Length == 0 ? (filter.Length == 0 ? "This folder is empty." : "No matching files or folders.") :
                $"{visible.Length} {(visible.Length == 1 ? "item" : "items")} · {openedDevice.DisplayName}";
        }

        async Task Load(Device device, string directory, int? historyTarget = null)
        {
            if (!Equals(CurrentReadyDevice, device)) return;
            var ticket = ++loading;
            status.Text = "Loading folder…";
            RemoteEntry[] result;
            try { result = ParseDirectory(directory, await readFiles(device, directory)); }
            catch when (ticket != loading) { return; }
            catch
            {
                path.Text = openedPath;
                status.Text = $"Could not open folder · {directory}";
                throw;
            }
            if (ticket != loading || !Equals(CurrentReadyDevice, device))
            {
                return;
            }
            if (historyTarget is { } index) historyIndex = index;
            else if (historyIndex < 0 || history[historyIndex] != directory)
            {
                history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
                history.Add(directory);
                historyIndex = history.Count - 1;
            }
            openedPath = directory;
            openedDevice = device;
            preparedSources = [];
            prepared = [];
            entries = folderEntries = result;
            searchResults = false;
            if (columns.Columns.Contains(location)) columns.Columns.Remove(location);
            nameColumn.Width = Math.Max(130, list.ActualWidth - 130);
            if (search.Text.Length > 0) search.Clear();
            Filter();
            UpdateAddress();
        }

        Device DirectoryDevice() => CurrentReadyDevice is { } device && Equals(openedDevice, device)
            ? device : throw new InvalidOperationException("Open a folder on the selected device first.");

        async Task OpenSelected()
        {
            if (Selection() is not [var item]) return;
            var device = DirectoryDevice();
            if (item.IsDirectory)
            {
                await Load(device, item.FullPath);
                return;
            }
            status.Text = $"Downloading a Windows copy of “{item.Name}”…";
            var folder = Directory.CreateDirectory(Path.Combine(DragRoot, $"EzAdb-drag-{Guid.NewGuid():N}")).FullName;
            var local = LocalChild(folder, item.Name);
            await PullPathsAsync(device, [item.FullPath], folder, askOverwrite: false);
            if (!Equals(openedDevice, device) || !list.IsVisible) return;
            var info = new OpenAsInfo { File = local, Flags = 0x04 };
            var result = SHOpenWithDialog(new WindowInteropHelper(this).Handle, ref info);
            if (result < 0 && result != unchecked((int)0x800704C7)) Marshal.ThrowExceptionForHR(result);
            Filter();
        }

        async Task SearchRecursive()
        {
            var device = DirectoryDevice();
            var directory = openedPath;
            var term = search.Text.Trim();
            if (term.Length == 0) throw new ArgumentException("Enter all or part of a file name.");
            var ticket = ++loading;
            var glob = "*" + string.Concat(term.Select(c => c is '\\' or '[' or ']' or '*' or '?' ? "\\" + c : c.ToString())) + "*";
            var query = $"find {Quote(directory)} -mindepth 1 -iname {Quote(glob)}";
            status.Text = "Searching subfolders…";
            string[] results;
            try
            {
                results = await Task.WhenAll(AndroidShell(device, query + " -type d -print0", seconds: 60),
                    AndroidShell(device, query + " ! -type d -print0", seconds: 60));
            }
            catch when (ticket != loading) { return; }
            catch { status.Text = "Could not finish searching subfolders."; throw; }
            if (ticket != loading || !Equals(CurrentReadyDevice, device) || term != search.Text.Trim()) return;
            entries = results.SelectMany((output, index) => output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Select(item => new RemoteEntry(Leaf(item), item, index == 0)))
                .OrderByDescending(item => item.IsDirectory).ThenBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
            searchResults = true;
            if (!columns.Columns.Contains(location)) columns.Columns.Add(location);
            nameColumn.Width = Math.Max(130, list.ActualWidth - 335);
            Filter();
        }

        async Task Push(string[] paths, string? folder = null)
        {
            var device = DirectoryDevice();
            var current = openedPath;
            var destination = folder ?? current;
            await PushPathsAsync(device, destination, paths, askOverwrite: true);
            if (openedPath == current && current == destination) await Load(device, current);
            else if (openedPath == current) status.Text = $"Transfer complete · {destination}";
        }

        async Task PrepareDrag()
        {
            var device = DirectoryDevice();
            var chosen = Selection();
            if (chosen.Length == 0)
            {
                return;
            }
            var sources = chosen.Select(item => item.FullPath).ToArray();
            if (sources.SequenceEqual(preparedSources) && prepared.Length == sources.Length)
            {
                return;
            }
            status.Text = "Downloading files for Windows drag and drop…";
            var folder = Directory.CreateDirectory(Path.Combine(DragRoot, $"EzAdb-drag-{Guid.NewGuid():N}"));
            var staged = new List<string>();
            for (var i = 0; i < sources.Length; i++)
            {
                var destination = Directory.CreateDirectory(Path.Combine(folder.FullName, i.ToString()));
                await PullPathsAsync(device, [sources[i]], destination.FullName, askOverwrite: false);
                staged.Add(LocalChild(destination.FullName, Leaf(sources[i])));
            }
            prepared = staged.ToArray();
            preparedSources = sources;
            status.Text = "Files are ready. Drag the selected items into a Windows folder.";
        }

        search.TextChanged += (_, _) =>
        {
            if (searchResults)
            {
                entries = folderEntries;
                searchResults = false;
                columns.Columns.Remove(location);
                nameColumn.Width = Math.Max(130, list.ActualWidth - 130);
            }
            Filter();
        };
        path.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await RunPageTask("Open folder", () => Load(RequireDevice(), PathOnDevice(path.Text)));
        };
        search.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await RunPageTask("Search subfolders", SearchRecursive);
        };
        list.MouseDoubleClick += async (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(list, source) is ListViewItem)
                await RunPageTask("Open", OpenSelected);
        };
        list.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(list);
        list.MouseMove += async (_, e) =>
        {
            var distance = e.GetPosition(list) - dragStart;
            if (dragging || e.LeftButton != MouseButtonState.Pressed ||
                (Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance &&
                 Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance) ||
                list.SelectedItems.Count == 0 || openedDevice is null)
            {
                return;
            }
            dragging = true;
            try
            {
                await RunPageTask("Prepare files", PrepareDrag);
                if (Mouse.LeftButton == MouseButtonState.Pressed && prepared.Length > 0 &&
                    Selection().Select(item => item.FullPath).SequenceEqual(preparedSources))
                {
                    DragDrop.DoDragDrop(list, new DataObject(DataFormats.FileDrop, prepared), DragDropEffects.Copy);
                }
            }
            finally { dragging = false; }
        };
        list.AllowDrop = true;
        list.DragOver += (_, e) =>
        {
            e.Effects = openedDevice is not null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        list.Drop += async (_, e) =>
        {
            e.Handled = true;
            if (!dragging && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                var row = e.OriginalSource is DependencyObject source ? ItemsControl.ContainerFromElement(list, source) as ListViewItem : null;
                var folder = row?.Content is RemoteEntry { IsDirectory: true } directory ? directory.FullPath : null;
                await RunPageTask("Transfer", () => Push(files, folder));
            }
        };

        async Task Create(bool directory)
        {
            var device = DirectoryDevice();
            var current = openedPath;
            if (AskText(directory ? "New folder name" : "New file name") is not { } name) return;
            var destination = Quote(Child(current, name));
            await AndroidShell(device, directory ? $"mkdir {destination}" : $"set -C; : > {destination}");
            if (openedPath == current) await Load(device, current);
        }
        async Task Rename()
        {
            var device = DirectoryDevice();
            var current = openedPath;
            if (Selection() is not [var item] || AskText("New name", item.Name) is not { } name || name == item.Name) return;
            var next = Quote(Child(Parent(item.FullPath), name));
            await AndroidShell(device, $"[ ! -e {next} ] && [ ! -L {next} ] && mv {Quote(item.FullPath)} {next} || {{ echo 'The destination exists or the rename failed' >&2; exit 1; }}");
            if (openedPath == current) await Load(device, current);
        }
        async Task Delete()
        {
            var device = DirectoryDevice();
            var current = openedPath;
            var chosen = Selection();
            if (chosen.Length == 0 || !Confirm($"Delete {chosen.Length} selected items on {device.DisplayName}? Folders and their contents will be deleted.", "Delete Android files")) return;
            await AndroidShell(device, "rm -rf -- " + string.Join(' ', chosen.Select(item => Quote(item.FullPath))), seconds: 60);
            if (openedPath == current) await Load(device, current);
        }
        async Task Download()
        {
            var device = DirectoryDevice();
            var chosen = Selection().Select(item => item.FullPath).ToArray();
            if (chosen.Length == 0 || BrowseDirectory("Choose a Windows destination folder") is not { } folder) return;
            await PullPathsAsync(device, chosen, folder, askOverwrite: true);
            status.Text = $"Transfer complete · {folder}";
        }
        async Task Upload()
        {
            _ = DirectoryDevice();
            var dialog = new OpenFileDialog { Title = "Select files to upload", Multiselect = true };
            if (dialog.ShowDialog(this) == true) await Push(dialog.FileNames);
        }
        ContextMenu Menu(params (string Label, Func<Task> Action)[] actions)
        {
            var menu = new ContextMenu { Background = Paint("Surface"), Foreground = Paint("Ink"), BorderBrush = Paint("Line") };
            foreach (var (label, action) in actions)
            {
                var item = new MenuItem { Header = label, Foreground = Paint("Ink") };
                item.Click += async (_, _) => await RunPageTask(label, action);
                menu.Items.Add(item);
            }
            return menu;
        }
        list.ContextMenu = Menu(("Open", OpenSelected), ("Save to PC", Download), ("Prepare for drag and drop", PrepareDrag),
            ("Rename", Rename), ("Delete", Delete));
        list.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(list, source) is ListViewItem row && !row.IsSelected)
            {
                list.SelectedItem = row.Content;
                row.Focus();
            }
        };
        list.ContextMenuOpening += (_, e) =>
        {
            var count = list.SelectedItems.Count;
            e.Handled = count == 0;
            ((MenuItem)list.ContextMenu.Items[0]).IsEnabled = count == 1;
            ((MenuItem)list.ContextMenu.Items[3]).IsEnabled = count == 1;
        };
        var places = UiStack(UiText("Quick access", true));
        places.Width = 154;
        places.Margin = new Thickness(0, 4, 12, 0);
        foreach (var (label, directory) in new[] { ("Internal storage", home), ("Downloads", home + "/Download"),
            ("Camera", home + "/DCIM"), ("Pictures", home + "/Pictures"), ("Documents", home + "/Documents"), ("Music", home + "/Music") })
        {
            var link = UiAction(label, () => Load(RequireDevice(), directory));
            link.Background = Brushes.Transparent;
            link.BorderBrush = Brushes.Transparent;
            link.Padding = new Thickness(8, 0, 8, 0);
            link.MinHeight = 34;
            link.Margin = new Thickness(0, 0, 4, 3);
            places.Children.Add(link);
        }
        var panes = new DockPanel();
        DockPanel.SetDock(places, Dock.Left);
        panes.Children.Add(places);
        panes.Children.Add(list);
        var addressBar = new DockPanel();
        addressBar.Children.Add(UiRow(back, forward, up, refresh));
        path.Margin = new Thickness(6, 1, 0, 5);
        path.VerticalAlignment = VerticalAlignment.Top;
        addressBar.Children.Add(path);
        var searchBar = new DockPanel();
        var recursive = UiAction("Search subfolders", SearchRecursive);
        recursive.VerticalAlignment = VerticalAlignment.Bottom;
        recursive.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(recursive, Dock.Right);
        searchBar.Children.Add(recursive);
        searchBar.Children.Add(Labeled("Filter files in this folder", search));
        var tools = UiRow(UiAction("New folder", () => Create(true)), UiAction("New file", () => Create(false)),
            UiAction("Upload", Upload), UiAction("Save to PC", Download),
            UiAction("File Explorer", () => { ShowExplorerOptions(); return Task.CompletedTask; }));
        var view = UiCard(addressBar, crumbs, searchBar, tools, panes, status);
        view.PreviewKeyDown += async (_, e) =>
        {
            var modifiers = Keyboard.Modifiers;
            Button? navigation = (e.SystemKey, modifiers) switch
            {
                (Key.Left, ModifierKeys.Alt) => back, (Key.Right, ModifierKeys.Alt) => forward, (Key.Up, ModifierKeys.Alt) => up, _ => null
            };
            if (e.Key == Key.F5) navigation = refresh;
            if (navigation is { IsEnabled: true })
            {
                e.Handled = true;
                navigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return;
            }
            if (modifiers == ModifierKeys.Control && e.Key is Key.L or Key.F)
            {
                e.Handled = true;
                var field = e.Key == Key.L ? path : search;
                field.Focus();
                field.SelectAll();
            }
            if (!list.IsKeyboardFocusWithin) return;
            Func<Task>? action = e.Key switch { Key.Enter => OpenSelected, Key.F2 => Rename, Key.Delete => Delete, _ => null };
            if (action is null) return;
            e.Handled = true;
            await RunPageTask("File action", action);
        };

        return new DevicePage(view, "Files", "Browse Android folders and drag files between EzAdb and Windows.", device =>
        {
            if (device is not null && Equals(openedDevice, device))
            {
                return;
            }
            loading++;
            openedDevice = null;
            openedPath = home;
            history.Clear();
            historyIndex = -1;
            searchResults = false;
            if (columns.Columns.Contains(location)) columns.Columns.Remove(location);
            nameColumn.Width = Math.Max(130, list.ActualWidth - 130);
            entries = folderEntries = [];
            prepared = [];
            preparedSources = [];
            search.Clear();
            Filter();
            UpdateAddress();
            status.Text = device is null ? "Choose an Android device." : "Choose a folder or enter an Android path above.";
            if (device is not null && loadFilesOnNavigation)
            {
                _ = RunPageTask("Load files", () => Load(device, home));
            }
        });
    }

    private void ShowExplorerOptions()
    {
        var usb = UiStack(UiText("USB", true), UiText("Unlock your phone and select “File transfer” in the USB notification. You’ll find the device under “This PC” in File Explorer."));
        var wireless = UiStack(UiText("Wireless access on Windows 11", true),
            UiText("Link your phone under “Mobile devices → Manage devices” using Link to Windows. Then enable “Show mobile device in File Explorer.” Requires Android 11 or later."));
        var open = new Button { Content = "Open File Explorer", Style = (Style)FindResource("Primary") };
        var settings = new Button { Content = "Open Mobile Devices settings", Style = (Style)FindResource("Action"), Margin = new Thickness(7, 0, 0, 0) };
        var view = UiStack(usb);
        var windows11 = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        if (windows11) view.Children.Add(wireless);
        view.Children.Add(windows11 ? UiRow(open, settings) : UiRow(open));
        var dialog = CreateToolDialog(this, "Android in File Explorer", 490, view);
        open.Click += async (_, _) => await RunPageTask("Open File Explorer", () =>
        {
            using var explorer = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                { UseShellExecute = true, ArgumentList = { "shell:MyComputerFolder" } });
            dialog.Close();
            return Task.CompletedTask;
        });
        settings.Click += async (_, _) => await RunPageTask("Open Mobile Devices settings", () =>
        {
            using var windows = Process.Start(new ProcessStartInfo("ms-settings:mobile-devices") { UseShellExecute = true });
            dialog.Close();
            return Task.CompletedTask;
        });
        dialog.ShowDialog();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        public string File;
        public string? Class;
        public uint Flags;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenWithDialog(nint owner, ref OpenAsInfo info);

    private async Task PushPathsAsync(Device device, string directory, string[] paths, bool askOverwrite)
    {
        await AndroidShell(device, $"[ -d {Quote(directory)} ] || {{ echo 'The destination folder does not exist' >&2; exit 1; }}");
        foreach (var path in paths)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            var target = Quote(Child(directory, name));
            var exists = await AndroidShell(device, $"if [ -e {target} ] || [ -L {target} ]; then echo yes; fi");
            if (exists.Trim() == "yes" && (!askOverwrite ||
                !Confirm($"“{name}” already exists on {device.DisplayName}. Overwrite files at the destination?", "Transfer file")))
            {
                if (!askOverwrite) throw new IOException($"Destination already exists: {name}");
                continue;
            }
            var result = await DeviceCommand(device, ["push", path, directory.TrimEnd('/') + "/"], seconds: 0);
            result.RequireSuccess();
            Log($"{device.DisplayName}: {name} transferred.");
        }
    }

    private async Task PullPathsAsync(Device device, string[] sources, string folder, bool askOverwrite, CancellationToken cancellation = default)
    {
        foreach (var source in sources)
        {
            var target = LocalChild(folder, Leaf(source));
            if ((File.Exists(target) || Directory.Exists(target)) &&
                (!askOverwrite || !Confirm($"“{Leaf(source)}” already exists at the destination. Overwrite existing files?", "Save file")))
            {
                if (!askOverwrite) throw new IOException($"Destination already exists: {target}");
                continue;
            }
            var result = await DeviceCommand(device, ["pull", source, folder], cancellation, seconds: 0);
            result.RequireSuccess();
            Log($"{device.DisplayName}: {Leaf(source)} saved to the PC.");
        }
    }

    private static string LocalChild(string folder, string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
        {
            throw new IOException($"“{name}” cannot be saved under this name on Windows.");
        }
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             "123456789¹²³".Contains(stem[3])))
        {
            throw new IOException($"“{name}” is reserved on Windows.");
        }
        return Path.Combine(folder, name);
    }

    private void DisposeDragCache()
    {
        // Explorer may still be copying after the drop returns. Keep recent staging directories until a later run.
        if (!Directory.Exists(DragRoot)) return;
        foreach (var folder in Directory.EnumerateDirectories(DragRoot, "EzAdb-drag-*"))
        {
            try
            {
                var directory = new DirectoryInfo(folder);
                if ((directory.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    directory.CreationTimeUtc < DateTime.UtcNow.AddDays(-2))
                {
                    directory.Delete(recursive: true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record InstalledApp(string Package, bool Disabled, bool System, string? Name = null)
    {
        public string Display => (Name is null ? Package : $"{Name}  ·  {Package}") +
                                 (Disabled ? "  ·  Disabled" : "") + (System ? "  ·  System" : "");
    }

    private DevicePage BuildAppsPage()
    {
        var search = UiField("Search by app name or package ID");
        var showSystem = UiCheck("Show system apps", true);
        var list = UiList("Installed Android apps", 283, display: nameof(InstalledApp.Display));
        var details = UiEditor("App info", 215, readOnly: true);
        details.Visibility = Visibility.Collapsed;
        var status = UiText("Choose an Android device.");
        InstalledApp[] apps = [];
        Device? loadedFor = null;
        var request = 0;

        void Filter()
        {
            var selected = (list.SelectedItem as InstalledApp)?.Package;
            var term = search.Text.Trim();
            var filtered = apps.Where(app => (showSystem.IsChecked == true || !app.System) &&
                (app.Package.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                 app.Name?.Contains(term, StringComparison.OrdinalIgnoreCase) == true)).ToArray();
            list.ItemsSource = filtered;
            if (selected is not null) list.SelectedItem = Array.Find(filtered, app => app.Package == selected);
        }

        search.TextChanged += (_, _) => Filter();
        showSystem.Checked += (_, _) => Filter();
        showSystem.Unchecked += (_, _) => Filter();

        async Task Load(Device device)
        {
            var ticket = ++request;
            status.Text = "Loading packages…";
            var results = await Task.WhenAll(AndroidShell(device, "pm list packages --user current"),
                AndroidShell(device, "pm list packages -s --user current"), AndroidShell(device, "pm list packages -d --user current"));
            if (ticket != request || !Equals(CurrentReadyDevice, device))
            {
                return;
            }
            var system = ParsePackages(results[1]).ToHashSet(StringComparer.Ordinal);
            var disabled = ParsePackages(results[2]).ToHashSet(StringComparer.Ordinal);
            loadedFor = device;
            apps = ParsePackages(results[0]).Select(package => new InstalledApp(package, disabled.Contains(package), system.Contains(package))).ToArray();
            Filter();
            status.Text = $"{apps.Length} packages · {device.DisplayName}";
            if (tools.Scrcpy is null || scrcpyIssue is not null) return;
            using var labels = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            labels.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var result = await tools.RunScrcpyAsync(device, "EzAdb App Names", ["--list-apps"], labels.Token);
                if (result.ExitCode != 0 || ticket != request || !Equals(CurrentReadyDevice, device)) return;
                var names = ParseAppNames(result.Details);
                apps = apps.Select(app => app with { Name = names.GetValueOrDefault(app.Package) })
                    .OrderBy(app => app.Name ?? app.Package, StringComparer.CurrentCultureIgnoreCase).ToArray();
                Filter();
            }
            catch (Exception error) when (error is OperationCanceledException or ToolDependencyException) { }
        }

        (Device Device, InstalledApp App) Pick() => CurrentReadyDevice is { } device && Equals(loadedFor, device) &&
            list.SelectedItem is InstalledApp app ? (device, app) : throw new InvalidOperationException("Choose an app from the device list.");

        async Task Action(string label, Func<InstalledApp, string> command, bool reload = false)
        {
            var (device, app) = Pick();
            await AndroidShell(device, command(app));
            Notify($"{label}: {app.Package}");
            if (reload) await Load(device);
        }

        var view = UiCard(Labeled("Search by app name or package ID", search), UiRow(showSystem,
                UiAction("Load apps", () => Load(RequireDevice()), true)), list, status,
            UiRow(UiAction("Start", () => Action("Started", app => Launch(app.Package))),
                UiAction("Stop", () => Action("Stopped", app => $"am force-stop --user current {Quote(app.Package)}")),
                UiAction("Aktivieren / deaktivieren", () => Action("App status updated",
                    app => $"pm {(app.Disabled ? "enable" : "disable-user")} --user current {Quote(app.Package)}", true)),
                UiAction("Uninstall", async () =>
                {
                    var (device, app) = Pick();
                    if (!Confirm($"Uninstall {app.Package} for the current Android user on {device.DisplayName}?", "Uninstall app"))
                    {
                        return;
                    }
                    var output = await AndroidShell(device, $"pm uninstall --user current {Quote(app.Package)}", seconds: 60);
                    if (!output.Trim().Equals("Success", StringComparison.Ordinal)) throw new InvalidOperationException(output);
                    Notify($"{app.Package} uninstalled.");
                    await Load(device);
                })),
            UiRow(UiAction("Export APKs", async () =>
                {
                    var (device, app) = Pick();
                    if (BrowseDirectory("Choose APK export folder") is not { } folder) return;
                    var packages = PrefixedLines(await AndroidShell(device, $"pm path --user current {Quote(app.Package)}"), "package:");
                    if (packages.Length == 0) throw new InvalidOperationException("Android did not return APK paths for this package.");
                    var target = Path.Combine(folder, $"{app.Package}-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
                    Directory.CreateDirectory(target);
                    await PullPathsAsync(device, packages, target, askOverwrite: false);
                    Notify($"APK export saved: {target}");
                }),
                UiAction("App info", async () =>
                {
                    var (device, app) = Pick();
                    var output = await AndroidShell(device, $"dumpsys package {Quote(app.Package)}");
                    if (Equals(CurrentReadyDevice, device) && Equals(list.SelectedItem, app))
                    {
                        details.Text = output.Trim();
                        details.Visibility = Visibility.Visible;
                    }
                })), details);
        list.SelectionChanged += (_, _) => { details.Visibility = Visibility.Collapsed; details.Clear(); };

        return new DevicePage(view, "Apps", "Manage installed apps and export base and split APKs.", device =>
        {
            if (Equals(loadedFor, device)) return;
            request++;
            loadedFor = null;
            apps = [];
            Filter();
            details.Visibility = Visibility.Collapsed;
            status.Text = device is null ? "Choose an Android device." : "Ready to load apps.";
            if (device is not null && allowDevicePolling)
            {
                _ = RunPageTask("Load apps", () => Load(device));
            }
        });
    }
}
