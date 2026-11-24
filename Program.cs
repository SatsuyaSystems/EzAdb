using System.Runtime.InteropServices;
using System.Windows;

namespace EzAdb;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Let EzAdb report Windows loader errors for its child tools in the setup dialog.
        SetErrorMode(0x0001 | 0x0002);
        var app = new Application();
        if (args is ["--self-test", ..])
        {
            return SelfTest.Run(args.ElementAtOrDefault(1), args.ElementAtOrDefault(2), args.ElementAtOrDefault(3));
        }

        return app.Run(new Dashboard());
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
