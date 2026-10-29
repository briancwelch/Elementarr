using System.Text.Json;
using System.Text.RegularExpressions;

namespace Elementarr.Services;

public sealed partial class UiService
{
    private readonly string path;
    private readonly SemaphoreSlim writer = new(1, 1);
    private UiSettings settings;
    public UiSettings Settings => Volatile.Read(ref settings);

    /// <summary>Loads server-wide appearance and wording independently of protected integration credentials.</summary>
    public UiService(IConfiguration configuration)
    {
        path = Path.Combine(Path.GetFullPath(configuration["ELEMENTARR_DATA"] ?? "data"), "appearance.json");
        settings = File.Exists(path) ? JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Stored appearance settings are invalid.") : new();
    }

    /// <summary>Validates safe appearance values and atomically stores plain-text wording overrides.</summary>
    public async Task SaveAsync(UiSettings value, CancellationToken ct)
    {
        foreach (var color in new[] { value.Canvas, value.Panel, value.Accent, value.Ink, value.Muted, value.Border })
            if (color is null || !Color().IsMatch(color)) throw new ArgumentException("Use six-digit hexadecimal colors.");
        foreach (var font in new[] { value.BodyFont, value.HeadingFont })
            if (font is null || !Font().IsMatch(font)) throw new ArgumentException("Use a font family name with letters, numbers, spaces, or hyphens.");
        if (value.FontSize is < 11 or > 22 || value.HeadingSize is < 24 or > 60 || value.NavWidth is < 180 or > 300
            || value.ContentWidth is < 1000 or > 3000 || value.CornerRadius is < 0 or > 24 || value.GridGap is < 8 or > 40
            || value.PosterSize is < 120 or > 320 || value.TimelineZoom is < 130 or > 360
            || value.PageSize is not (10 or 25 or 50 or 100))
            throw new ArgumentException("An appearance setting is outside its supported range.");
        if (value.Texts is null || value.Texts.Count > 1000 || value.Texts.Any(x => !TextKey().IsMatch(x.Key)
            || x.Value is null || x.Value.Length > 2000 || x.Value.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))))
            throw new ArgumentException("Text overrides must use valid keys and no more than 2,000 characters per value.");
        await writer.WaitAsync(ct);
        try
        {
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(value), ct);
            File.Move(path + ".tmp", path, true);
            Volatile.Write(ref settings, value);
        }
        finally { writer.Release(); }
    }

    /// <summary>Allows color values without permitting arbitrary CSS injection.</summary>
    [GeneratedRegex("^#[a-fA-F0-9]{6}$")]
    private static partial Regex Color();

    /// <summary>Restricts optional Google Fonts requests to a family name rather than a URL or stylesheet.</summary>
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9 -]{0,79}$")]
    private static partial Regex Font();

    /// <summary>Accepts stable UI wording identifiers without allowing HTML property paths.</summary>
    [GeneratedRegex("^[a-zA-Z0-9_.-]{1,100}$")]
    private static partial Regex TextKey();
}
