namespace Elementarr;

public sealed record UiSettings
{
    public string Canvas { get; init; } = "#101723";
    public string Panel { get; init; } = "#172233";
    public string Accent { get; init; } = "#64dfd2";
    public string Ink { get; init; } = "#edf3fc";
    public string Muted { get; init; } = "#97a9c2";
    public string Border { get; init; } = "#2b3b52";
    public string BodyFont { get; init; } = "Segoe UI";
    public string HeadingFont { get; init; } = "Segoe UI";
    public bool GoogleFonts { get; init; }
    public int FontSize { get; init; } = 14;
    public int HeadingSize { get; init; } = 36;
    public int NavWidth { get; init; } = 224;
    public int ContentWidth { get; init; } = 2200;
    public int CornerRadius { get; init; } = 9;
    public int GridGap { get; init; } = 18;
    public int PosterSize { get; init; } = 175;
    public int PageSize { get; init; } = 25;
    public int TimelineZoom { get; init; } = 220;
    public bool ShowPosters { get; init; } = true;
    public bool ShowBackdrops { get; init; } = true;
    public bool ShowRatings { get; init; } = true;
    public bool ShowReasons { get; init; } = true;
    public bool ShowFooter { get; init; } = true;
    public bool Animations { get; init; } = true;
    public Dictionary<string, string> Texts { get; init; } = [];
}
