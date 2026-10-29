using Elementarr;

// Probe the running process from Docker's health check without requiring a shell or curl in the image.
if (args.Contains("--healthcheck"))
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync("http://127.0.0.1:8080/health");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException) { return 1; }
    catch (TaskCanceledException) { return 1; }
}

// Native shortcuts may start in a different working directory; assets belong beside the executable.
if (OperatingSystem.IsWindows()) Directory.SetCurrentDirectory(AppContext.BaseDirectory);
// Return an ordinary failure code so Windows Error Reporting cannot hold a failed background process open.
try { await AppHost.Build(args).RunAsync(); return 0; }
catch (Exception exception)
{
    Console.Error.WriteLine($"Elementarr could not start ({exception.GetType().Name}). Check the configured port, data directory, and application logs.");
    return 1;
}
