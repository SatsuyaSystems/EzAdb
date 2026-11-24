using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace EzAdb;

internal static class ToolInstaller
{
    private const string Version = "5.0.1";
    private static readonly (string Platform, string Sha256) Package = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => ("win64", "b12a2c4ee8be317422451fc7dcf8ee20a71b5ea7ef9ad73ddd825a25316227a5"),
        Architecture.Arm64 => ("winarm64", "16c3fc2068df64946f670e3504b4752073ebae47a416a45b46e79a5ab19c689b"),
        Architecture.X86 => ("win32", "0cb65ebf1bce892fa4d1502d699af0c7a1a7f24d7febcb9ef72894e3f23fc2ad"),
        _ => ("", "")
    };
    private static string PackageName => $"scrcpy-{Package.Platform}-v{Version}";

    public static async Task<AdbTools> InstallAsync(string root, IProgress<string>? progress, CancellationToken cancellation)
    {
        if (Package.Platform.Length == 0)
        {
            throw new PlatformNotSupportedException("Für diese Windows-Architektur ist kein automatisches Paket verfügbar.");
        }

        Directory.CreateDirectory(root);
        progress?.Report("Suche nach bereits eingerichteten Tools …");
        foreach (var directory in Directory.EnumerateDirectories(root, $"{PackageName}*"))
        {
            if (await ReadyAsync(directory, cancellation).ConfigureAwait(false) is { } existing)
            {
                return existing;
            }
        }

        progress?.Report("ADB und scrcpy werden heruntergeladen …");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2), MaxResponseContentBufferSize = 40 * 1024 * 1024 };
        using var response = await client.GetAsync(
            $"https://github.com/Genymobile/scrcpy/releases/download/v{Version}/{PackageName}.zip", cancellation)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var download = await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
        return await InstallArchiveAsync(download, root, progress, cancellation).ConfigureAwait(false);
    }

    internal static async Task<AdbTools> InstallArchiveAsync(byte[] download, string root, IProgress<string>? progress,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        progress?.Report("Prüfsumme wird geprüft …");
        if (!Convert.ToHexString(SHA256.HashData(download)).Equals(Package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Die Prüfsumme des Downloads stimmt nicht. Starte die Einrichtung erneut.");
        }

        var staging = Path.Combine(root, $".setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            progress?.Report("Das geprüfte Paket wird entpackt …");
            await Task.Run(() =>
            {
                using var stream = new MemoryStream(download, writable: false);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                archive.ExtractToDirectory(staging);
            }, cancellation).ConfigureAwait(false);

            var unpacked = Path.Combine(staging, PackageName);
            progress?.Report("ADB und scrcpy werden geprüft …");
            if (await ReadyAsync(unpacked, cancellation).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException("Die entpackten Tools konnten nicht gestartet werden.");
            }

            cancellation.ThrowIfCancellationRequested();
            var destination = Path.Combine(root, PackageName);
            try
            {
                Directory.Move(unpacked, destination);
            }
            catch (IOException) when (Directory.Exists(destination))
            {
                // Reuse a concurrent installation, or leave a damaged version untouched while repairing it.
                if (await ReadyAsync(destination, cancellation).ConfigureAwait(false) is { } existing)
                {
                    return existing;
                }
                destination += $"-{Guid.NewGuid():N}";
                Directory.Move(unpacked, destination);
            }
            return new AdbTools(destination);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static async Task<AdbTools?> ReadyAsync(string folder, CancellationToken cancellation)
    {
        if (!File.Exists(Path.Combine(folder, "adb.exe")) || !File.Exists(Path.Combine(folder, "scrcpy.exe")))
        {
            return null;
        }
        var candidate = new AdbTools(folder);
        var issues = await Task.WhenAll(candidate.CheckAsync(false, cancellation), candidate.CheckAsync(true, cancellation))
            .ConfigureAwait(false);
        return issues.All(issue => issue is null) ? candidate : null;
    }
}
