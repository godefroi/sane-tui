using SaneTui.Sane;

namespace SaneTui.Web;

/// <summary>Everything a page needs to draw itself. Sent whole on connect and after every change, so a reload or a second tab is never behind.</summary>
internal sealed record Snapshot(long Revision, string Status, double? Progress, IReadOnlyList<LogLine> Log, DocumentInfo? Document, PreviewInfo? Preview);

internal sealed record LogLine(DateTimeOffset At, string Text, bool Error);

internal sealed record PageInfo(string Id, int Width, int Height);

/// <summary>A document's trip to Paperless. Status is consuming (Paperless has it and is working), done, failed, or unknown (it never said).</summary>
/// <param name="TaskId">Paperless's task for consuming it.</param>
/// <param name="DocumentId">The document Paperless made, once it has.</param>
/// <param name="Message">What went wrong, or what Paperless said.</param>
internal sealed record SendInfo(string TaskId, string Status, int? DocumentId, string? Message, DateTimeOffset At);

internal sealed record DocumentInfo(string Id, IReadOnlyList<PageInfo> Pages, SendInfo? Sent = null);

/// <param name="Version">Changes with each new preview, so the browser doesn't show a cached one.</param>
internal sealed record PreviewInfo(int Version, SaneScanArea Area, int PixelWidth, int PixelHeight, double? Dpi);

internal sealed record ChoiceSet(IReadOnlyList<string> Choices, string Selected);

/// <summary>An on/off option the scanner has (a driver's own, such as one that straightens pages in the feeder), offered as a checkbox.</summary>
internal sealed record SwitchOption(string Name, string Title, string Description, bool Value);

internal sealed record Capabilities(string Device, ChoiceSet? Sources, ChoiceSet? Modes, IReadOnlyList<string> Feeders, IReadOnlyList<double> Resolutions, double DefaultResolution, IReadOnlyList<string> Papers, IReadOnlyList<SwitchOption> Switches);

internal sealed record PreviewRequest(string Device, string? Source);

/// <param name="Left">Where the area starts, in millimeters on the scanner's bed (not as seen on a rotated preview).</param>
/// <param name="Settings">Other scanner options to set, as <c>name=value</c> (only on/off options are offered by the page).</param>
internal sealed record ScanJobRequest(string Device, string? Source, string? Mode, double Resolution, string? Paper, double? Left, double? Top, double? Width, double? Height, bool AllPages, bool Rotate180, bool Deskew = false, bool Trim = false, IReadOnlyList<string>? Settings = null);
