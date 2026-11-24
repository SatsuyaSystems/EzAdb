using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace EzAdb;

internal static partial class DeviceCommands
{
    public static string[] Target(Device device) => device.TransportId.Length == 0
        ? ["-s", device.Serial] : ["-t", device.TransportId];

    public static string[] Shell(string command) => ["shell", "-T", command];

    public static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public static string PathOnDevice(string value)
    {
        if (!value.StartsWith('/') || value.Contains('\0'))
        {
            throw new ArgumentException("Gib einen absoluten Android-Pfad ein, zum Beispiel /sdcard/Download.");
        }
        var parts = new List<string>();
        foreach (var part in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }
        return "/" + string.Join('/', parts);
    }

    public static string Child(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
        {
            throw new ArgumentException("Enter a valid file name without slashes.");
        }
        return directory.TrimEnd('/') + "/" + name;
    }

    public static string Parent(string path) => path[..Math.Max(0, path.LastIndexOf('/'))] is { Length: > 0 } parent ? parent : "/";
    public static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

    public static string ListDirectory(string path)
    {
        var p = Quote(path);
        return $"if [ ! -d {p} ] || [ ! -r {p} ] || [ ! -x {p} ]; then echo 'Folder is not accessible' >&2; exit 1; fi; " +
               $"cd {p} || exit 1; for p in .[!.]* ..?* *; do " +
               "[ -e \"$p\" ] || [ -L \"$p\" ] || continue; " +
               "if [ -d \"$p\" ]; then t=d; else t=f; fi; printf '%s\\0%s\\0' \"$t\" \"$p\"; done; :";
    }

    internal sealed record RemoteEntry(string Name, string FullPath, bool IsDirectory)
    {
        public string Kind => IsDirectory ? "Folder" : System.IO.Path.GetExtension(Name) is { Length: > 1 } extension
            ? extension[1..].ToUpperInvariant() + " file" : "File";
        public string Folder => Parent(FullPath);
    }

    public static RemoteEntry[] ParseDirectory(string path, string output)
    {
        var parts = output.Split('\0');
        var entries = new List<RemoteEntry>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            if (parts[i] is ("d" or "f") && parts[i + 1].Length > 0 && !parts[i + 1].Contains('/'))
            {
                entries.Add(new RemoteEntry(parts[i + 1], path.TrimEnd('/') + "/" + parts[i + 1], parts[i] == "d"));
            }
        }
        return entries.OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string[] ParsePackages(string output) => PrefixedLines(output, "package:")
        .Where(IsPackage).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public static Dictionary<string, string> ParseAppNames(string output)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        string? continuedName = null;
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal))
            {
                var columns = Regex.Match(line[2..], @"^(.*?)\s{2,}(\S+)$");
                if (columns.Success && IsPackage(columns.Groups[2].Value))
                {
                    names[columns.Groups[2].Value] = columns.Groups[1].Value;
                    continuedName = null;
                }
                else
                {
                    continuedName = line[2..];
                }
            }
            else
            {
                if (continuedName is not null && IsPackage(line))
                    names[line] = continuedName;
                continuedName = null;
            }
        }
        return names;
    }

    public static string[] PrefixedLines(string output, string prefix) =>
        output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).Select(line => line[prefix.Length..]).ToArray();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)*$")]
    private static partial Regex PackagePattern();

    public static bool IsPackage(string package) => PackagePattern().IsMatch(package);

    public static string RequirePackage(string package) => IsPackage(package.Trim())
        ? package.Trim() : throw new ArgumentException("Enter a package ID, for example com.android.settings.");

    public static string Launch(string package, int? display = null) =>
        $"am start --user current {(display is { } id ? $"--display {id} " : "")}" +
        $"-a android.intent.action.MAIN -c android.intent.category.LAUNCHER -p {Quote(RequirePackage(package))}";

    public static void CheckAndroid(CommandResult result) => result.RequireSuccess(output =>
        !output.Split('\n', StringSplitOptions.TrimEntries).Any(line =>
            line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Error type ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Failure", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Exception occurred", StringComparison.OrdinalIgnoreCase)));

    public static string InputPrefix(int? display) => display is { } id ? $"input -d {id}" : "input";

    public static string TypeText(string text, int? display = null)
    {
        if (text.Length == 0 || text.Any(c => c is not ('\r' or '\n' or '\t') && (c < ' ' || c > '~')))
        {
            throw new ArgumentException("ADB text input supports ASCII characters. Paste Unicode text directly into the scrcpy window.");
        }
        var commands = new List<string>();
        var input = InputPrefix(display);
        var segment = new System.Text.StringBuilder();
        void Flush()
        {
            if (segment.Length > 0)
            {
                commands.Add($"{input} text " + Quote(segment.ToString().Replace(" ", "%s", StringComparison.Ordinal)));
                segment.Clear();
            }
        }
        foreach (var c in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
        {
            if (c is '\n' or '\t' or '%')
            {
                Flush();
                commands.Add(c switch { '\n' => $"{input} keyevent 66", '\t' => $"{input} keyevent 61", _ => $"{input} text '%'" });
            }
            else
            {
                segment.Append(c);
            }
        }
        Flush();
        return string.Join(" && ", commands);
    }

    public static string[] ParseAdbArguments(string line, bool deviceOnly)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Contains('\0'))
        {
            throw new ArgumentException("Enter an ADB command.");
        }
        var argv = CommandLineToArgvW("adb " + line, out var count);
        if (argv == IntPtr.Zero)
        {
            throw new ArgumentException("Could not read the ADB command.");
        }
        try
        {
            var args = Enumerable.Range(1, count - 1)
                .Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? "").ToArray();
            if (args.FirstOrDefault() is { } executable && (executable.Equals("adb", StringComparison.OrdinalIgnoreCase) ||
                executable.Equals("adb.exe", StringComparison.OrdinalIgnoreCase)))
            {
                args = args[1..];
            }
            if (args.Length == 0 || (deviceOnly && (args[0].StartsWith('-') ||
                args[0] is "connect" or "disconnect" or "pair" or "kill-server" or "start-server" or "devices" or "mdns" or "reconnect")))
            {
                throw new ArgumentException("Use a device-specific ADB command without global ADB options.");
            }
            return args;
        }
        finally { LocalFree(argv); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
