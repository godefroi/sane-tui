namespace SaneTui.Web;

/// <summary>Settings for suggesting a title and date for a document (configuration section <c>Naming</c>). Without an endpoint, key and model the feature is off.</summary>
internal sealed class NamingOptions
{
	/// <summary>
	/// The OpenAI-compatible endpoint of an Azure OpenAI or Microsoft Foundry resource: either the resource's address
	/// (<c>https://example.openai.azure.com/</c>) or the full <c>.../openai/v1/</c> address.
	/// </summary>
	public Uri? Endpoint { get; set; }

	public string? ApiKey { get; set; }

	/// <summary>The model to ask. On Azure and Foundry this is the name given to the deployment.</summary>
	public string? Model { get; set; }

	/// <summary>How many pages, from the first, are read for text to go on. A title and a date are nearly always on the first one or two.</summary>
	public int Pages { get; set; } = 5;

	/// <summary>Tesseract language codes, joined with +, for the text on the pages.</summary>
	public string Languages { get; set; } = "eng";

	public bool Available => Endpoint is not null && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Model);

	/// <summary>The endpoint with the <c>openai/v1/</c> path the OpenAI library needs, added if it wasn't given.</summary>
	public Uri? BaseUri
	{
		get {
			if (Endpoint is null || Endpoint.AbsolutePath.Contains("/openai/v1", StringComparison.OrdinalIgnoreCase)) {
				return Endpoint;
			}

			return new Uri(Endpoint.GetLeftPart(UriPartial.Authority) + "/openai/v1/");
		}
	}
}

/// <summary>Suggesting a name failed, or isn't possible; the message says why, in words for the person using the page.</summary>
internal sealed class NamingException(string message, Exception? inner = null) : Exception(message, inner);
