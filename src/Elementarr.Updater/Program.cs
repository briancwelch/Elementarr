using System.Reflection;
using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using Elementarr.UpdateCore;

return await Updater.RunAsync(args);

internal static class Updater
{
    /// <summary>Accepts only private installation-scoped signed jobs and rolls back an unhealthy service update.</summary>
    internal static async Task<int> RunAsync(string[] args)
    {
        string? report = null;
        try
        {
            if (args.Length != 1) throw new ArgumentException("The updater requires one private job directory.");
            var install = NativeInstallation.Read();
            var updates = Path.GetFullPath(Path.Combine(install.Data, "updates"));
            var job = Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(job), updates, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(job), "N", out _) || (File.GetAttributes(job) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The update job is outside the registered private staging directory.");
            report = Path.Combine(job, "result.json");
            var manifest = ReleaseTrust.Verify(await File.ReadAllBytesAsync(Path.Combine(job, "elementarr-update.json")),
                Convert.FromBase64String(await File.ReadAllTextAsync(Path.Combine(job, "elementarr-update.sig"))));
            var app = Path.Combine(install.Root, "app");
            var current = AssemblyName.GetAssemblyName(Path.Combine(app, "Elementarr.dll")).Version!;
            if (Version.Parse(manifest.Version) <= current) throw new InvalidDataException("The updater will not reinstall or downgrade the running application.");
            await ReleaseTrust.VerifyPayloadAsync(Path.Combine(job, "update.zip"), manifest, default);
            var staged = Path.Combine(job, "payload");
            ReleaseTrust.Extract(Path.Combine(job, "update.zip"), staged);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { success = false, stage = "installing", version = manifest.Version }));
            // Let the triggering API request finish before the service is stopped.
            await Task.Delay(2000);
            using var service = new ServiceController("Elementarr");
            var dataBackup = Path.Combine(job, "previous-data");
            var dataCopied = false;
            await UpdateInstaller.ApplyAsync(staged, app, Path.Combine(job, "previous-app"),
                () => StopAsync(service, app), () => StartAsync(service), () => HealthyAsync(install, manifest.Version),
                () => { CopyData(install.Data, dataBackup); dataCopied = true; return Task.CompletedTask; },
                () => { if (dataCopied) RestoreData(dataBackup, install.Data); return Task.CompletedTask; });
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { success = true, stage = "complete", version = manifest.Version, completedAt = DateTimeOffset.UtcNow }));
            return 0;
        }
        catch (Exception exception)
        {
            if (report is not null) await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { success = false, stage = "failed", error = exception.GetType().Name, message = "Update failed; the previous application was retained or restored. Check service health before retrying." }));
            Console.Error.WriteLine("Elementarr update failed: " + exception.GetType().Name);
            return 1;
        }
    }

    /// <summary>Stops only Elementarr through Service Control Manager and waits for its executable to release installed files.</summary>
    private static Task StopAsync(ServiceController service, string app)
    {
        var processes = Process.GetProcessesByName("Elementarr");
        try
        {
            List<Process> owned = [];
            foreach (var process in processes)
            {
                try { if (string.Equals(process.MainModule?.FileName, Path.Combine(app, "Elementarr.exe"), StringComparison.OrdinalIgnoreCase)) owned.Add(process); }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException) { /* Other user processes are not update targets. */ }
            }
            service.Refresh();
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
            }
            foreach (var process in owned) if (!process.WaitForExit(15000)) throw new IOException("The stopped service process has not released its application files.");
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return Task.CompletedTask;
    }

    /// <summary>Backs up quiescent persistent data without recursing into update jobs or copying transient logs.</summary>
    private static void CopyData(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (name is "updates" or "logs") continue;
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Data backup cannot follow filesystem links.");
            if (Directory.Exists(entry)) CopyData(entry, Path.Combine(destination, name));
            else File.Copy(entry, Path.Combine(destination, name), true);
        }
    }

    /// <summary>Restores only backed-up installation data after failed startup, keeping the private update evidence and logs.</summary>
    private static void RestoreData(string source, string destination)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in Directory.EnumerateFileSystemEntries(destination))
        {
            if (Path.GetFileName(entry) is "updates" or "logs") continue;
            if (!Path.GetFullPath(entry).StartsWith(root, StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe data restore target.");
            if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry);
        }
        CopyData(source, destination);
    }

    /// <summary>Starts only the existing registered Elementarr service without changing its identity or binary command.</summary>
    private static Task StartAsync(ServiceController service)
    {
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped) service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(45));
        return Task.CompletedTask;
    }

    /// <summary>Requires the updated process to report the signed target version over its local health endpoint.</summary>
    private static async Task<bool> HealthyAsync(NativePaths install, string version)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var host = install.Bind == "0.0.0.0" ? "127.0.0.1" : install.Bind;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var response = await client.GetAsync($"http://{host}:{install.Port}/health");
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (response.IsSuccessStatusCode && document.RootElement.GetProperty("version").GetString() == version) return true;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException) { /* Retry while the service starts. */ }
            await Task.Delay(1000);
        }
        return false;
    }
}
