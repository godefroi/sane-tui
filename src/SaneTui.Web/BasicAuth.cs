using System.Security.Cryptography;
using System.Text;

namespace SaneTui.Web;

internal static class BasicAuth
{
	/// <summary>
	/// Whether an <c>Authorization</c> header carries the expected credentials. Both parts are always compared, in constant time
	/// (over hashes, so the lengths don't leak either), so a wrong answer doesn't reveal which part was wrong or how close it was.
	/// </summary>
	public static bool Matches(string? header, string user, string password)
	{
		const string Scheme = "Basic ";

		if (header is null || !header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) {
			return false;
		}

		string decoded;

		try {
			decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[Scheme.Length..].Trim()));
		} catch (FormatException) {
			return false;
		}

		var colon = decoded.IndexOf(':');

		if (colon < 0) {
			return false;
		}

		var userMatches = Same(decoded[..colon], user);
		var passwordMatches = Same(decoded[(colon + 1)..], password);

		return userMatches & passwordMatches;
	}

	private static bool Same(string a, string b) =>
		CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
