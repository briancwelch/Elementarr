namespace Elementarr;

public sealed record ReleaseNote(string Version, string Date, string Title, string[] Changes);

public static class ReleaseNotes
{
    public static IReadOnlyList<ReleaseNote> All { get; } = [
        new("0.5.0", "2026-10-29", "A more cohesive Elementarr", [
            "Grouped movie and TV navigation, upcoming Sonarr episodes, separate affected TV history, and a built-in changelog.",
            "Atom-and-clapperboard branding, matching favicon and Windows icons, a version footer, and Plex profile pictures.",
            "Broader typography controls, consistent themed scrollbars and sliders, and configurable badge corners.",
            "Live search, a branded sign-out confirmation, and timed notifications with countdown bars and a five-second default."
        ]),
        new("0.4.1", "2026-10-29", "Public automatic updates", [
            "Signed public updates work without a GitHub account or token, with automatic checks and installation enabled by default.",
            "Legacy update tokens are discarded while saved preferences are retained. Installation documentation is simpler."
        ]),
        new("0.4.0", "2026-10-29", "Windows service and television", [
            "Standalone Windows service, installer host/port setup, tray controls, and signed updates with rollback.",
            "Independent movie and TV history, working Sonarr additions, expanded TV metadata, and source poster fallbacks.",
            "Persistent movie-history removal and clearer TMDB setup instructions."
        ])
    ];
}
