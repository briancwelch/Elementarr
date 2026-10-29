using System.Net;
using System.Text.Json;

namespace Elementarr.Services;

public sealed record ServerSettings(int Port = 8090, string BindAddress = "127.0.0.1", string HostName = "localhost");

public sealed class ServerSettingsStore
{
    private readonly string path;
    private readonly SemaphoreSlim mutation = new(1, 1);
    public ServerSettings Settings { get; private set; }
    public ServerSettings Running { get; }
    public bool Container { get; } = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";

    /// <summary>Loads native hosting preferences without overriding Docker's container listener or explicit URL arguments.</summary>
    public ServerSettingsStore(string directory)
    {
        path = Path.Combine(directory, "server.json");
        Settings = File.Exists(path) ? JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Stored server preferences are invalid.") : new();
        Validate(Settings);
        Running = Settings;
    }

    /// <summary>Persists the next native listener; callers explicitly restart after saving to avoid interrupting requests.</summary>
    public async Task SaveAsync(ServerSettings value, CancellationToken ct)
    {
        if (Container) throw new ArgumentException("Change ELEMENTARR_PORT in Docker Compose's .env file and recreate the container.");
        Validate(value);
        await mutation.WaitAsync(ct);
        try
        {
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(value), ct);
            File.Move(path + ".tmp", path, true);
            Settings = value;
            if (OperatingSystem.IsWindows())
            {
                using var installKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Elementarr");
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Elementarr\Listener", writable: true);
                if (key is not null && string.Equals(installKey?.GetValue("DataDirectory") as string, Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue("Port", value.Port, Microsoft.Win32.RegistryValueKind.DWord);
                    key.SetValue("BindAddress", value.BindAddress);
                    key.SetValue("HostName", value.HostName);
                }
            }
        }
        finally { mutation.Release(); }
    }

    /// <summary>Allows a valid TCP port and either local-only or explicit LAN access.</summary>
    private static void Validate(ServerSettings value)
    {
        if (value.HostName.Length > 253 || Uri.CheckHostName(value.HostName) is not (UriHostNameType.Dns or UriHostNameType.IPv4) || value.HostName.Contains(':'))
            throw new ArgumentException("Enter a hostname such as localhost or radium.local, without a protocol, path, or port.");
        if (value.BindAddress == "127.0.0.1" && !value.HostName.Equals("localhost", StringComparison.OrdinalIgnoreCase) && value.HostName != "127.0.0.1")
            throw new ArgumentException("Use localhost for local-only access, or choose Local network for a network hostname.");
        if (value.Port is < 1024 or > 65535) throw new ArgumentException("Choose a port between 1024 and 65535.");
        if (!IPAddress.TryParse(value.BindAddress, out _) || value.BindAddress is not ("127.0.0.1" or "0.0.0.0"))
            throw new ArgumentException("Choose local-only (127.0.0.1) or LAN (0.0.0.0) access.");
    }
}
