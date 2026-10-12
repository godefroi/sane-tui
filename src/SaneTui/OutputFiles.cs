using System.Text.RegularExpressions;

namespace SaneTui;

/// <summary>What to do when a scan's output files already exist. Existing files are never replaced unless this says so.</summary>
internal enum ExistingFiles
{
	/// <summary>Refuse to scan. Nothing is touched, and no sheet is used up.</summary>
	Fail,

	/// <summary>Replace the existing files.</summary>
	Overwrite,

	/// <summary>Leave the existing files alone and save under new names (<c>scan (2).jpg</c>).</summary>
	Rename,
}

/// <summary>Finding out whether a scan would write over files that are already there.</summary>
internal static class OutputFiles
{
	/// <summary>
	/// The existing files a scan to <paramref name="output"/> would replace: the file itself, or for a batch every file named like
	/// the pages it would write (<c>scan-001.jpg</c>, <c>scan-002.jpg</c>, ...), since how many pages there will be isn't known beforehand.
	/// </summary>
	public static IReadOnlyList<string> Existing(string output, bool batch)
	{
		if (!batch) {
			return File.Exists(output) ? [output] : [];
		}

		var folder = Path.GetDirectoryName(Path.GetFullPath(output))!;

		if (!Directory.Exists(folder)) {
			return [];
		}

		var stem = Path.GetFileNameWithoutExtension(output);
		var extension = Path.GetExtension(output);
		var pageName = new Regex($"^{Regex.Escape(stem)}-[0-9]{{3,}}{Regex.Escape(extension)}$");

		// the wildcard is only a first filter; file systems differ in how they match extensions, so the exact check is the regex
		return [.. Directory.EnumerateFiles(folder, $"{stem}-*{extension}").Where(f => pageName.IsMatch(Path.GetFileName(f))).Order()];
	}

	/// <summary>
	/// The output name to use so that nothing already there is in the way: <c>scan (2).jpg</c>, or <c>scan (3).jpg</c> if that's taken too.
	/// For a batch the whole series moves together (<c>scan (2)-001.jpg</c>, <c>scan (2)-002.jpg</c>, ...), so its pages stay together.
	/// Returns <paramref name="output"/> unchanged if it's already free.
	/// </summary>
	public static string FreeName(string output, bool batch)
	{
		if (Existing(output, batch).Count == 0) {
			return output;
		}

		var folder = Path.GetDirectoryName(output) ?? "";
		var stem = Path.GetFileNameWithoutExtension(output);
		var extension = Path.GetExtension(output);

		for (var n = 2; n <= 10_000; n++) {
			var candidate = Path.Combine(folder, $"{stem} ({n}){extension}");

			if (Existing(candidate, batch).Count == 0) {
				return candidate;
			}
		}

		throw new IOException($"No free file name was found next to {output}.");
	}

	/// <summary>A sentence saying which files are in the way, for a message to the user.</summary>
	public static string Describe(IReadOnlyList<string> existing, bool batch)
	{
		var first = Path.GetFileName(existing[0]);

		return (batch, existing.Count) switch {
			(false, _) => $"{first} already exists.",
			(true, 1) => $"{first} already exists.",
			(true, _) => $"{first} and {existing.Count - 1} other numbered file(s) already exist.",
		};
	}
}
