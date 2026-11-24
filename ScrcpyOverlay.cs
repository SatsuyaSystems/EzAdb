using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EzAdb;

internal sealed class ScrcpyOverlay : IDisposable
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly nint owner;
    private Rect? lastBounds;
    private bool closed;
    internal Window Window { get; }

    public ScrcpyOverlay(Process process, Func<Window, FrameworkElement> content)
    {
        process.Refresh();
        owner = process.MainWindowHandle;
        if (!TryGetBounds(out var bounds))
            throw new InvalidOperationException("The Android screen window is not ready yet.");
        if (IsIconic(owner) || !IsWindowVisible(owner))
            throw new InvalidOperationException("Bring the Android screen window forward to set click points.");

        var dpi = GetDpiForWindow(owner);
        var scale = dpi == 0 ? 1 : dpi / 96.0;
        Window = new Window
        {
            Title = "EzAdb Click Points", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
            Width = bounds.Width / scale, Height = bounds.Height / scale, Left = bounds.Left / scale, Top = bounds.Top / scale
        };
        var interop = new WindowInteropHelper(Window) { Owner = owner };
        Window.Content = content(Window);
        Window.SourceInitialized += (_, _) => Move(interop.Handle, bounds);
        Window.Closed += (_, _) => { closed = true; timer.Stop(); };
        Window.Show();
        lastBounds = bounds;
        timer.Tick += Update;
        if (!closed) timer.Start();
    }

    public void Dispose()
    {
        if (!closed) Window.Close();
    }

    private void Update(object? sender, EventArgs e)
    {
        if (!IsWindow(owner))
        {
            Dispose();
            return;
        }
        if (IsIconic(owner) || !IsWindowVisible(owner))
        {
            Window.Hide();
            lastBounds = null;
            return;
        }
        if (!TryGetBounds(out var bounds)) return;
        if (!Window.IsVisible) Window.Show();
        if (lastBounds != bounds)
        {
            Move(new WindowInteropHelper(Window).Handle, bounds);
            lastBounds = bounds;
        }
    }

    private bool TryGetBounds(out Rect bounds)
    {
        bounds = default;
        if (!GetClientRect(owner, out var client) || client.Right <= 0 || client.Bottom <= 0) return false;
        var origin = new NativePoint();
        if (!ClientToScreen(owner, ref origin)) return false;
        bounds = new Rect(origin.X, origin.Y, client.Right, client.Bottom);
        return true;
    }

    private static void Move(nint window, Rect bounds) =>
        SetWindowPos(window, 0, (int)bounds.Left, (int)bounds.Top, (int)bounds.Width, (int)bounds.Height, 0x0014);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
