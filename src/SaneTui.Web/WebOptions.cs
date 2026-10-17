namespace SaneTui.Web;

/// <summary>Settings from configuration (section <c>Web</c>, so environment variables like <c>Web__Password</c>).</summary>
internal sealed class WebOptions
{
	/// <summary>Where documents (the scanned pages) are kept. Pages survive a restart.</summary>
	public string DataFolder { get; set; } = "data";

	public string User { get; set; } = "scan";

	/// <summary>The password for basic authentication. Required: the service refuses to start without one unless <see cref="AllowAnonymous"/> is set.</summary>
	public string? Password { get; set; }

	/// <summary>Skip authentication entirely. For local development only.</summary>
	public bool AllowAnonymous { get; set; }
}
