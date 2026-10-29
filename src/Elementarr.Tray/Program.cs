using System.Diagnostics;
using System.ServiceProcess;
using Elementarr.UpdateCore;

namespace Elementarr.Tray;

internal static class Program
{
    /// <summary>Runs one interactive tray companion per user session while the Windows service runs independently.</summary>
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\Elementarr.Tray", out var first);
        if (!first) return;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext(args.Contains("--open")));
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 3000 };
    private readonly ToolStripMenuItem status = new("Elementarr") { Enabled = false };
    private readonly ToolStripMenuItem address = new("Server address") { Enabled = false };
    private readonly ToolStripMenuItem start = new("Start service");
    private readonly ToolStripMenuItem stop = new("Stop service");
    private readonly ToolStripMenuItem restart = new("Restart service");
    private bool busy;

    /// <summary>Builds native tray controls with an explicit distinction between stopping the service and exiting the tray.</summary>
    internal TrayContext(bool openOnStart)
    {
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open Elementarr"); open.Click += async (_, _) => await OpenAsync();
        start.Click += async (_, _) => await ControlAsync("start");
        stop.Click += async (_, _) => await ControlAsync("stop");
        restart.Click += async (_, _) => await ControlAsync("restart");
        var updates = new ToolStripMenuItem("Updates and settings"); updates.Click += async (_, _) => await OpenAsync(true);
        var exit = new ToolStripMenuItem("Exit tray (keep service running)"); exit.Click += (_, _) => ExitThread();
        var stopExit = new ToolStripMenuItem("Stop service and exit"); stopExit.Click += async (_, _) => { if (await ControlAsync("stop")) ExitThread(); };
        menu.Items.AddRange([status, address, new ToolStripSeparator(), open, start, stop, restart, updates, new ToolStripSeparator(), stopExit, exit]);
        icon = new NotifyIcon { Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Elementarr.ico")), Text = "Elementarr", ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += async (_, _) => await OpenAsync();
        timer.Tick += (_, _) => RefreshStatus();
        RefreshStatus(); timer.Start();
        if (openOnStart) _ = OpenAsync();
    }

    /// <summary>Shows the current registered URL and service status without reading protected connections or watch history.</summary>
    private void RefreshStatus()
    {
        try
        {
            var settings = NativeInstallation.Read();
            var url = $"http://{settings.Host}:{settings.Port}";
            using var service = new ServiceController("Elementarr");
            status.Text = "Service: " + service.Status;
            address.Text = url;
            icon.Text = ("Elementarr · " + service.Status).Length <= 63 ? "Elementarr · " + service.Status : "Elementarr";
            start.Enabled = !busy && service.Status == ServiceControllerStatus.Stopped;
            stop.Enabled = restart.Enabled = !busy && service.Status == ServiceControllerStatus.Running;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        { status.Text = "Service unavailable"; start.Enabled = stop.Enabled = restart.Enabled = false; }
    }

    /// <summary>Starts the service if needed and opens its configured host/port in the user's normal browser.</summary>
    private async Task OpenAsync(bool updates = false)
    {
        if (!await ControlAsync("ensure")) return;
        var settings = NativeInstallation.Read();
        _ = Process.Start(new ProcessStartInfo($"http://{settings.Host}:{settings.Port}/" + (updates ? "?settings=updates" : "")) { UseShellExecute = true });
    }

    /// <summary>Uses the installer-granted start/stop rights for this service only, reporting failures in the interactive user session.</summary>
    private async Task<bool> ControlAsync(string action)
    {
        if (busy) return false;
        busy = true; RefreshStatus();
        try
        {
            await Task.Run(() =>
            {
                using var service = new ServiceController("Elementarr");
                if (action is "stop" or "restart" && service.Status != ServiceControllerStatus.Stopped)
                { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45)); }
                service.Refresh();
                if (action is "start" or "restart" or "ensure" && service.Status == ServiceControllerStatus.Stopped)
                { service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(45)); }
            });
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException)
        { MessageBox.Show("Elementarr could not " + action + " the service. Check the Windows service and installation permissions.", "Elementarr", MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
        finally { busy = false; RefreshStatus(); }
    }

    /// <summary>Removes the notification icon when the user exits, without implicitly stopping the separate Windows service.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Stop(); timer.Dispose(); icon.Visible = false; icon.ContextMenuStrip?.Dispose(); icon.Icon?.Dispose(); icon.Dispose(); }
        base.Dispose(disposing);
    }
}
