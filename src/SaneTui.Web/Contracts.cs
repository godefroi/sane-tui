using SaneTui.Sane;

namespace SaneTui.Web;

/// <summary>Everything a page needs to draw itself. Sent whole on connect and after every change, so a reload or a second tab is never behind.</summary>
internal sealed record Snapshot(long Revision, string Status, double? Progress, IReadOnlyList<LogLine> Log, DocumentInfo? Document, PreviewInfo? Preview);

internal sealed record LogLine(DateTimeOffset At, string Text, bool Error);

internal sealed record PageInfo(string Id, int Width, int Height);

internal sealed record DocumentInfo(string Id, IReadOnlyList<PageInfo> Pages);

/// <param name="Version">Changes with each new preview, so the browser doesn't show a cached one.</param>
internal sealed record PreviewInfo(int Version, SaneScanArea Area, int PixelWidth, int PixelHeight, double? Dpi);

internal sealed record ChoiceSet(IReadOnlyList<string> Choices, string Selected);

internal sealed record Capabilities(string Device, ChoiceSet? Sources, ChoiceSet? Modes, IReadOnlyList<string> Feeders, IReadOnlyList<double> Resolutions, double DefaultResolution, IReadOnlyList<string> Papers);

internal sealed record PreviewRequest(string Device, string? Source);

/// <param name="Left">Where the area starts, in millimeters on the scanner's bed (not as seen on a rotated preview).</param>
internal sealed record ScanJobRequest(string Device, string? Source, string? Mode, double Resolution, string? Paper, double? Left, double? Top, double? Width, double? Height, bool AllPages, bool Rotate180);
