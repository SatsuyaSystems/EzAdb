using System.Security.Cryptography;
using QRCoder;

namespace EzAdb;

internal sealed class QrPairing
{
    public string ServiceName { get; } = $"studio-{Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant()}";
    public string Password { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));

    public byte[] CreatePng()
    {
        var payload = $"WIFI:T:ADB;S:{ServiceName};P:{Password};;";
        using var data = QRCodeGenerator.GenerateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(data);
        return qr.GetGraphic(7);
    }

    public async Task<string> PairAsync(AdbTools tools, IProgress<string> progress, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var services = await tools.RunAdbAsync(["mdns", "services"], cancellation, 12).ConfigureAwait(false);
            services.RequireSuccess();
            var endpoint = FindEndpoint(services.Output, ServiceName);
            if (endpoint is not null)
            {
                progress.Report("Device found. Pairing…");
                return await tools.PairAsync(endpoint, Password, cancellation).ConfigureAwait(false);
            }
            await Task.Delay(900, cancellation).ConfigureAwait(false);
        }
    }

    internal static string? FindEndpoint(string output, string serviceName)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields is [var instance, var type, var address] &&
                instance.Equals(serviceName, StringComparison.OrdinalIgnoreCase) &&
                type.TrimEnd('.') == "_adb-tls-pairing._tcp" && AdbTools.IsEndpoint(address))
            {
                return address;
            }
        }
        return null;
    }

    internal static string? ReadGuid(string output)
    {
        var start = output.IndexOf("[guid=", StringComparison.Ordinal);
        var end = start >= 0 ? output.IndexOf(']', start) : -1;
        return end > start + 6 ? output[(start + 6)..end] : null;
    }

    internal static bool IsPairedDevice(Device device, string guid)
    {
        const string suffix = "._adb-tls-connect._tcp";
        if (!device.Ready || !device.Serial.TrimEnd('.').EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var instance = device.Serial.TrimEnd('.')[..^suffix.Length];
        return instance.Equals(guid, StringComparison.OrdinalIgnoreCase)
            || instance.StartsWith($"{guid}-", StringComparison.OrdinalIgnoreCase);
    }
}
