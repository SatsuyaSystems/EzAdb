using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace EzAdb;

internal sealed partial class Dashboard
{
    private sealed record WifiProfile(string Address, string Name, string Serial);

    internal sealed record WifiRow(string Address, string Name, string Status, bool Connected, bool CanToggle, bool CanRemove)
    {
        public string Detail => $"{Address} · {Status}";
        public string Action => Connected ? "Disconnect" : "Connect";
    }

    private const string TlsConnectSuffix = "._adb-tls-connect._tcp";
    private WifiProfile[] wifiProfiles = [];
    private WifiRow[] shownWifiProfiles = [];
    private string WifiProfilesFile => Path.Combine(LocalData, "wifi-devices.json");

    private void LoadWifiProfiles()
    {
        if (ReadSetting(WifiProfilesFile) is { } json)
        {
            try
            {
                wifiProfiles = (JsonSerializer.Deserialize<WifiProfile[]>(json) ?? [])
                    .Where(profile => profile is not null && profile.Address is not null && profile.Serial is not null &&
                        profile.Name is not null && AdbTools.IsWifiAddress(profile.Address) && AdbTools.IsWifiAddress(profile.Serial))
                    .DistinctBy(profile => AdbTools.NormalizeWifiAddress(profile.Address), StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (JsonException) { Log("Could not read saved Wi-Fi devices."); }
        }
        RenderWifiProfiles();
    }

    private static bool SameWifiAddress(string left, string right) =>
        AdbTools.NormalizeWifiAddress(left).Equals(AdbTools.NormalizeWifiAddress(right), StringComparison.OrdinalIgnoreCase);

    private static bool MatchesSavedService(string address, string candidate)
    {
        if (SameWifiAddress(address, candidate)) return true;
        var saved = AdbTools.NormalizeWifiAddress(address);
        var discovered = AdbTools.NormalizeWifiAddress(candidate);
        return saved.EndsWith(TlsConnectSuffix, StringComparison.OrdinalIgnoreCase) &&
               discovered.EndsWith(TlsConnectSuffix, StringComparison.OrdinalIgnoreCase) &&
               discovered.StartsWith(saved[..^TlsConnectSuffix.Length] + "-", StringComparison.OrdinalIgnoreCase);
    }

    private Device? FindWifiTransport(WifiProfile profile)
    {
        var networkDevices = knownDevices.Where(device => device.IsWifi).ToArray();
        var exact = Array.Find(networkDevices, device => SameWifiAddress(device.Serial, profile.Serial) ||
            SameWifiAddress(device.Serial, profile.Address));
        if (exact is not null) return exact;
        return networkDevices.Where(device => MatchesSavedService(profile.Serial, device.Serial)).ToArray() is [var discovered]
            ? discovered : null;
    }

    private void RenderWifiProfiles()
    {
        var enabled = !busy && !checkingTools;
        var rows = wifiProfiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(profile => profile.Address, StringComparer.OrdinalIgnoreCase).Select(profile =>
            {
                var transport = FindWifiTransport(profile);
                return new WifiRow(profile.Address, profile.Name.Length == 0 ? "Wi-Fi device" : profile.Name,
                    transport?.StatusLabel ?? "Disconnected", transport is not null, enabled && adbIssue is null && tools.Adb is not null, enabled);
            }).ToArray();
        if (!shownWifiProfiles.SequenceEqual(rows))
        {
            shownWifiProfiles = rows;
            SavedWifiRows.ItemsSource = rows;
        }
        NoSavedWifi.Visibility = Visible(rows.Length == 0);
    }

    private void SaveWifiProfiles(IEnumerable<WifiProfile> profiles)
    {
        var updated = profiles.ToArray();
        if (!wifiProfiles.SequenceEqual(updated))
        {
            wifiProfiles = updated;
            try { SaveSetting(WifiProfilesFile, JsonSerializer.Serialize(wifiProfiles)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Notify("The Wi-Fi device list could only be saved for this session.", true, error.Message);
            }
        }
        RenderWifiProfiles();
    }

    private static void UpdateWifiProfile(List<WifiProfile> profiles, string address, string serial, string name)
    {
        if (!AdbTools.IsWifiAddress(address) || !AdbTools.IsWifiAddress(serial)) return;
        address = AdbTools.NormalizeWifiAddress(address);
        serial = AdbTools.NormalizeWifiAddress(serial);
        var index = profiles.FindIndex(profile => SameWifiAddress(profile.Address, address) ||
            SameWifiAddress(profile.Serial, serial) || SameWifiAddress(profile.Address, serial));
        if (index < 0)
        {
            var aliases = profiles.Where(profile => MatchesSavedService(profile.Serial, serial)).ToArray();
            if (aliases is [var alias]) index = profiles.IndexOf(alias);
        }
        if (index < 0)
        {
            profiles.Add(new WifiProfile(address, name, serial));
        }
        else
        {
            var existing = profiles[index];
            profiles[index] = existing with { Name = name.Length > 0 ? name : existing.Name, Serial = serial };
        }
    }

    private void RememberConnectedWifi(Device[] previous)
    {
        var profiles = wifiProfiles.ToList();
        foreach (var device in knownDevices.Where(device => device.IsWifi && device.Ready && !previous.Contains(device)))
        {
            UpdateWifiProfile(profiles, device.Serial, device.Serial, device.Model);
        }
        SaveWifiProfiles(profiles);
    }

    private void RememberWifiPairing(string response)
    {
        if (QrPairing.ReadGuid(response) is not { } guid) return;
        var device = Array.Find(knownDevices, candidate => candidate.IsWifi && QrPairing.IsPairedDevice(candidate, guid));
        var address = device?.Serial ?? guid + TlsConnectSuffix;
        var profiles = wifiProfiles.ToList();
        UpdateWifiProfile(profiles, address, address, device?.Model ?? "");
        SaveWifiProfiles(profiles);
    }

    internal static string? ConnectedWifiAddress(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            const string connected = "connected to ";
            const string already = "already connected to ";
            var address = line.StartsWith(already, StringComparison.Ordinal) ? line[already.Length..] :
                          line.StartsWith(connected, StringComparison.Ordinal) ? line[connected.Length..] : null;
            if (address is not null && AdbTools.IsWifiAddress(address)) return address;
        }
        return null;
    }

    internal static string? FindAdvertisedWifiAddress(string output, string saved)
    {
        var addresses = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            var row = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (row is not [var instance, var type, var endpoint] ||
                type.TrimEnd('.') is not ("_adb-tls-connect._tcp" or "_adb._tcp") || !AdbTools.IsEndpoint(endpoint))
                continue;
            var address = $"{instance}.{type.TrimEnd('.')}";
            if (SameWifiAddress(saved, address)) return address;
            if (AdbTools.IsWifiAddress(address) && MatchesSavedService(saved, address)) addresses.Add(address);
        }
        return addresses.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() is [var match] ? match : null;
    }

    private async Task ConnectSavedWifiAsync(string address)
    {
        if (!AdbTools.IsWifiAddress(address)) throw new InvalidOperationException("The Wi-Fi address is invalid.");
        Notify($"Connecting to {address} …");
        var result = await tools.RunAdbAsync(["connect", address], lifetime.Token, 25);
        var serial = ConnectedWifiAddress(result.Output);
        if (serial is null && !AdbTools.IsEndpoint(address))
        {
            var discovered = await tools.RunAdbAsync(["mdns", "services"], lifetime.Token, 8);
            if (discovered.ExitCode == 0 && FindAdvertisedWifiAddress(discovered.Output, address) is { } candidate &&
                !SameWifiAddress(candidate, address))
            {
                result = await tools.RunAdbAsync(["connect", candidate], lifetime.Token, 25);
                serial = ConnectedWifiAddress(result.Output);
            }
        }
        result.RequireSuccess(_ => serial is not null);
        var profiles = wifiProfiles.ToList();
        UpdateWifiProfile(profiles, address, serial!, "");
        SaveWifiProfiles(profiles);
        await RefreshAsync();
        if (lifetime.IsCancellationRequested) return;
        var deviceIndex = Array.FindIndex(knownDevices, device => device.IsWifi && SameWifiAddress(device.Serial, serial!));
        if (deviceIndex >= 0)
        {
            Devices.SelectedIndex = deviceIndex;
            var device = knownDevices[deviceIndex];
            Notify($"{device.DisplayName}: {device.StatusLabel} · Wi-Fi", !device.Ready);
        }
        else
        {
            Notify($"Saved Wi-Fi address: {address}");
        }
    }

    private async void OnToggleSavedWifi(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is not WifiRow row ||
            Array.Find(wifiProfiles, profile => SameWifiAddress(profile.Address, row.Address)) is not { } selected)
            return;
        var connected = FindWifiTransport(selected);
        await RunActionAsync(connected is null ? "Connect via Wi-Fi" : "Disconnect Wi-Fi", async () =>
        {
            if (connected is null)
            {
                await ConnectSavedWifiAsync(selected.Address);
                return;
            }
            var result = await tools.RunAdbAsync(["disconnect", connected.Serial], lifetime.Token, 20);
            result.RequireSuccess(output => output.StartsWith("disconnected ", StringComparison.Ordinal));
            await RefreshAsync();
            Notify($"{connected.DisplayName}: Wi-Fi disconnected.");
        });
    }

    private void OnRemoveSavedWifi(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is WifiRow row && !busy)
        {
            SaveWifiProfiles(wifiProfiles.Where(profile => !SameWifiAddress(profile.Address, row.Address)));
        }
    }
}
