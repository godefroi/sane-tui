using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SaneTui.Web;

/// <summary>
/// Reads the text of scanned pages by running the <c>tesseract</c> program, once per page. Running the program rather than
/// loading its libraries into the process means the distribution's own build is used as it is, with no native libraries to
/// line up with the right names.
/// </summary>
internal partial class Tesseract(IOptions<NamingOptions> options)
{
	// enough for any title and date; a longer text only costs the language model more to read
	private const int MaxCharacters = 16 * 1024;

	private static readonly TimeSpan PageTimeout = TimeSpan.FromMinutes(2);

	/// <summary>The text of the first pages of the document, in order, stopping when there's enough.</summary>
	/// <exception cref="NamingException">Tesseract isn't installed or failed.</exception>
	public virtual async Task<string> ReadAsync(IReadOnlyList<string> pageFiles, CancellationToken cancellationToken)
	{
		var languages = options.Value.Languages;

		// the value goes on a command line; it's checked rather than trusted
		if (!LanguageCodes().IsMatch(languages)) {
			throw new NamingException($"'{languages}' isn't a list of Tesseract language codes (like eng or eng+deu).");
		}

		var text = new StringBuilder();

		foreach (var file in pageFiles.Take(Math.Max(1, options.Value.Pages))) {
			text.AppendLine(await RunAsync(file, languages, cancellationToken));

			if (text.Length > MaxCharacters) {
				break;
			}
		}

		return text.ToString();
	}

	private static async Task<string> RunAsync(string file, string languages, CancellationToken cancellationToken)
	{
		// --psm 1: automatic page layout with detection of the page's orientation, since a feeder can deliver a sheet any way up
		var start = new ProcessStartInfo("tesseract") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };

		foreach (var argument in new[] { file, "stdout", "-l", languages, "--psm", "1" }) {
			start.ArgumentList.Add(argument);
		}

		Process process;

		try {
			process = Process.Start(start)!;
		} catch (Win32Exception ex) {
			throw new NamingException("Reading the pages needs Tesseract, and it isn't installed here (the program 'tesseract').", ex);
		}

		using (process) {
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

			timeout.CancelAfter(PageTimeout);

			// both streams are drained while waiting, or a program that writes a lot to one of them blocks forever (and they end when the program does, killed or not)
			var output = process.StandardOutput.ReadToEndAsync();
			var errors = process.StandardError.ReadToEndAsync();

			try {
				await process.WaitForExitAsync(timeout.Token);
			} catch (OperationCanceledException) {
				process.Kill(entireProcessTree: true);

				if (cancellationToken.IsCancellationRequested) {
					throw;
				}

				throw new NamingException($"Tesseract took more than {PageTimeout.TotalMinutes:0} minutes to read a page, so it was stopped.");
			}

			var text = await output;
			var error = await errors;

			if (process.ExitCode != 0) {
				var reason = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? $"exit code {process.ExitCode}";

				throw new NamingException($"Tesseract couldn't read a page: {reason}");
			}

			return text;
		}
	}

	[GeneratedRegex(@"^[A-Za-z0-9_]{2,32}(\+[A-Za-z0-9_]{2,32})*$")]
	private static partial Regex LanguageCodes();
}
