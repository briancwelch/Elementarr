using Microsoft.Win32;
namespace Elementarr.UpdateCore;

public sealed record NativePaths(string Root, string Data, string Host, int Port, string Bind);

public static class NativeInstallation
{
    /// <summary>Reads administrator-created installation coordinates used by the service, tray, and fixed updater helper.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static NativePaths Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Elementarr") ?? throw new InvalidDataException("Windows service installation is missing.");
        using var listener = key.OpenSubKey("Listener");
        return new((string?)key.GetValue("InstallRoot") ?? throw new InvalidDataException("Installation directory is missing."),
            (string?)key.GetValue("DataDirectory") ?? throw new InvalidDataException("Data directory is missing."),
            (string?)listener?.GetValue("HostName") ?? "localhost", (int?)listener?.GetValue("Port") ?? 8090, (string?)listener?.GetValue("BindAddress") ?? "127.0.0.1");
    }
}
