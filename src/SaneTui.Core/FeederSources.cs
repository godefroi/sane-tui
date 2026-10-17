namespace SaneTui;

/// <summary>
/// Whether a scan source is a document feeder. Source names are free text that differs between drivers ("ADF Front",
/// "Automatic Document Feeder"), so this is a guess from the name, good enough to choose a sensible default.
/// </summary>
internal static class FeederSources
{
	public static bool Looks(string? source) =>
		source is not null && (source.Contains("ADF", StringComparison.OrdinalIgnoreCase) || source.Contains("feeder", StringComparison.OrdinalIgnoreCase));
}
