using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

namespace EzAdb;

internal sealed record Device(string Serial, string State, string Model, string TransportId, string UsbPath = "")
{
    public bool Ready => State == "device";
    public string DisplayName => Model.Length == 0 ? Serial : Model;
    public bool IsEmulator => UsbPath.Length == 0 && Serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase);
    public bool IsWifi => UsbPath.Length == 0 && !IsEmulator && AdbTools.IsWifiAddress(Serial);
    public string ConnectionLabel => IsEmulator ? "Emulator" : IsWifi ? "Wi-Fi" : "USB";
    public string ConnectedName => $"{DisplayName} · {ConnectionLabel}";
    public string StatusLabel => State switch
    {
        "device" => "Connected",
        "unauthorized" => "Authorization required",
        "offline" => "Offline",
        _ => State
    };

    public override string ToString() => Model.Length == 0 ? Serial : $"{Model} · {Serial}";
}

internal sealed record CommandResult(int ExitCode, string Output, string Error, byte[] Data)
{
    public string Details => string.Join(Environment.NewLine,
        new[] { Output.Trim(), Error.Trim() }.Where(line => line.Length != 0));

    public void RequireSuccess(Func<string, bool>? matches = null)
    {
        if (ExitCode != 0 || (matches is not null && !matches(Output.Trim())))
        {
            throw new InvalidOperationException(Details.Length > 0 ? Details : $"Exit code {ExitCode}");
        }
    }
}

internal sealed class ToolDependencyException(string tool, string message) : Exception(message)
{
    public string Tool { get; } = tool;
}

internal sealed class AdbTools
{
    public string? Adb { get; }
    public string? Scrcpy { get; }

    public AdbTools(string? preferredFolder = null)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools");
        var directories = new[] { preferredFolder, bundled, AppContext.BaseDirectory }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        Scrcpy = Find("scrcpy.exe", directories);
        Adb = Find("adb.exe", new[] { preferredFolder, Path.GetDirectoryName(Scrcpy), bundled,
            Path.Combine(AppContext.BaseDirectory, "platform-tools"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android", "Sdk", "platform-tools") }.Concat(directories));
    }

    private static string? Find(string executable, IEnumerable<string?> directories)
    {
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory.Trim('"')))
            {
                continue;
            }

            var path = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public static Device[] ParseDevices(string output) =>
        (from line in output.Split('\n', StringSplitOptions.TrimEntries)
         let fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
         where fields.Length >= 2 && fields[0] != "List" && !fields[0].StartsWith('*')
         let model = Value(fields, "model:").Replace('_', ' ')
         select new Device(fields[0], fields[1], model, Value(fields, "transport_id:"), Value(fields, "usb:"))).ToArray();

    private static string Value(string[] fields, string prefix) =>
        fields.Skip(2).FirstOrDefault(field => field.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..] ?? "";

    public static bool IsWifiAddress(string value)
    {
        if (IsEndpoint(value))
        {
            return true;
        }

        var service = NormalizeWifiAddress(value);
        return service.Length > 0 && service[0] != '-' &&
            service.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') &&
            (service.EndsWith("._adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase) ||
             service.EndsWith("._adb._tcp", StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeWifiAddress(string value)
    {
        var address = value.Trim().TrimEnd('.');
        return address.EndsWith("._tcp.local", StringComparison.OrdinalIgnoreCase) ? address[..^".local".Length] : address;
    }

    public static bool IsEndpoint(string value)
    {
        var separator = value.LastIndexOf(':');
        if (separator < 1 || !int.TryParse(value.AsSpan(separator + 1),
                System.Globalization.NumberStyles.None, null, out var port) || port is < 1 or > 65535)
        {
            return false;
        }

        var host = value[..separator];
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            return IPAddress.TryParse(host[1..^1], out var ip) &&
                ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        }

        return Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4;
    }

    public Task<CommandResult> RunAdbAsync(string[] arguments, CancellationToken cancellation,
        int seconds = 20, bool binary = false, string? input = null, IProgress<string>? progress = null) =>
        RunAsync(RequireExecutable(Adb, "adb.exe"), arguments, cancellation, seconds, binary, input, progress: progress);

    public Task<CommandResult> RunDeviceAsync(Device device, string[] arguments, CancellationToken cancellation,
        int seconds = 20, bool binary = false, IProgress<string>? progress = null) =>
        RunAdbAsync([..DeviceCommands.Target(device), ..arguments], cancellation, seconds, binary, progress: progress);

    public Task<CommandResult> RunScrcpyAsync(Device device, string title, string[] options,
        CancellationToken cancellation, IProgress<string>? progress = null, Action<Process>? started = null) =>
        RunAsync(RequireScrcpy(), ["--serial", device.Serial, "--window-title", title, ..options], cancellation,
            adbOverride: Adb, closeWindow: true, progress: progress, started: started);

    public static string? FindFfmpeg() => Find("ffmpeg.exe", new[] { Path.Combine(AppContext.BaseDirectory, "tools"),
        AppContext.BaseDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links") }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)));

    public static Task<CommandResult> ExtractFrameAsync(string ffmpeg, string video, string png, CancellationToken cancellation) =>
        RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", video, "-frames:v", "1", png],
            cancellation, seconds: 30);

    public async Task<string> PairAsync(string address, string password, CancellationToken cancellation)
    {
        var result = await RunAdbAsync(["pair", address], cancellation, 30, input: password).ConfigureAwait(false);
        result.RequireSuccess(output => output.Split('\n').Any(line =>
            line.Contains("Successfully paired to ", StringComparison.Ordinal) &&
            !line.Contains("Failed:", StringComparison.OrdinalIgnoreCase)));
        return result.Output;
    }

    public Task<CommandResult> MirrorAsync(Device device, bool keepAudioOnDevice, CancellationToken cancellation,
        Action<Process>? started = null) =>
        RunAsync(RequireScrcpy(), MirrorArguments(device, keepAudioOnDevice), cancellation, adbOverride: Adb, closeWindow: true, started: started);

    internal static string[] MirrorArguments(Device device, bool keepAudioOnDevice)
    {
        string[] arguments = ["--serial", device.Serial, "--window-title", $"EzAdb · {device}"];
        return keepAudioOnDevice ? [..arguments, "--no-audio"] : arguments;
    }

    public async Task<string?> CheckAsync(bool mirror, CancellationToken cancellation)
    {
        try
        {
            var result = mirror
                ? await RunAsync(RequireScrcpy(), ["--version"], cancellation, 8).ConfigureAwait(false)
                : await RunAdbAsync(["version"], cancellation, 8).ConfigureAwait(false);
            result.RequireSuccess(output => output.StartsWith(mirror ? "scrcpy " : "Android Debug Bridge", StringComparison.Ordinal));
            return null;
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        {
            return error.Message;
        }
    }

    private string RequireScrcpy()
    {
        var executable = RequireExecutable(Scrcpy, "scrcpy.exe");
        var server = Environment.GetEnvironmentVariable("SCRCPY_SERVER_PATH");
        var actualPath = File.ResolveLinkTarget(executable, returnFinalTarget: true)?.FullName ?? executable;
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(actualPath)!, "scrcpy-server")) && !File.Exists(server))
        {
            throw new ToolDependencyException("scrcpy.exe", "scrcpy-server is missing from the extracted folder.");
        }
        return executable;
    }

    private static string RequireExecutable(string? executable, string name)
    {
        if (!File.Exists(executable))
        {
            throw new ToolDependencyException(name, $"{name} was not found.");
        }
        return executable!;
    }

    internal static bool IsDependencyError(int exitCode) => unchecked((uint)exitCode) is
        0xC0000135 or 0xC0000138 or 0xC0000139 or 0xC000007B or 0xC0000142;

    private static Process StartProcess(ProcessStartInfo start)
    {
        try
        {
            return Process.Start(start) ?? throw new InvalidOperationException("Could not start the process.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3 or 126 or 127 or 193 or 1157 or 14001)
        {
            throw new ToolDependencyException(Path.GetFileName(start.FileName), error.Message);
        }
    }

    private static void StopProcess(Process process, bool closeWindow = false)
    {
        try
        {
            if (!process.HasExited && (!closeWindow || !process.CloseMainWindow()))
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static async Task<CommandResult> RunAsync(string executable, string[] arguments,
        CancellationToken cancellation, int seconds = 0, bool binary = false, string? input = null,
        string? adbOverride = null, bool closeWindow = false, IProgress<string>? progress = null, Action<Process>? started = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (seconds > 0)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        }

        var token = timeout.Token;
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        if (adbOverride is not null)
        {
            start.Environment["ADB"] = adbOverride;
        }

        using var process = StartProcess(start);
        using var stop = token.Register(() => StopProcess(process, closeWindow));
        using var data = new MemoryStream();
        var output = binary ? Task.FromResult("") : ReadOutputAsync(process.StandardOutput, progress, token);
        var copy = binary ? process.StandardOutput.BaseStream.CopyToAsync(data, token) : Task.CompletedTask;
        var error = ReadOutputAsync(process.StandardError, progress, token);
        try
        {
            started?.Invoke(process);
            if (input is not null)
            {
                await process.StandardInput.WriteLineAsync(input.AsMemory(), token).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(output, copy, error).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (IsDependencyError(process.ExitCode))
            {
                var name = Path.GetFileName(executable);
                throw new ToolDependencyException(name, $"A dependency for {name} is missing or incompatible (0x{process.ExitCode:X8}).");
            }
            return new CommandResult(process.ExitCode, await output, await error, data.ToArray());
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"No response within {seconds} seconds.");
        }
        finally
        {
            if (closeWindow && token.IsCancellationRequested && !process.HasExited)
            {
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { await process.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (grace.IsCancellationRequested) { }
            }
            StopProcess(process);
            if (closeWindow && !process.HasExited)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, IProgress<string>? progress, CancellationToken token)
    {
        if (progress is null)
        {
            return await reader.ReadToEndAsync(token).ConfigureAwait(false);
        }
        var output = new StringBuilder();
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            progress.Report(line);
            output.AppendLine(line);
            if (output.Length > 24_000)
            {
                output.Remove(0, output.Length - 16_000);
            }
        }
        return output.ToString();
    }
}
