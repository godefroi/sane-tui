using System.Globalization;
using System.Text;

namespace SaneTui.Web;

/// <summary>
/// Writes a PDF with one page per JPEG picture. A PDF can hold a JPEG exactly as it is (the DCTDecode filter), so each file's
/// bytes are copied in untouched: nothing is compressed a second time, and the original picture can be pulled back out of the PDF
/// byte for byte. (Magick.NET's PDF writer was tried first and re-encodes the pictures.)
/// </summary>
internal static class PdfBuilder
{
	// for a picture that doesn't say how many dots per inch it has: a typical document scan
	private const double FallbackDpi = 150;

	/// <summary>
	/// Makes the PDF as a temporary file that deletes itself when the stream is closed, so a long document isn't held in memory.
	/// Each page is the size of its picture at the resolution recorded in it.
	/// </summary>
	public static Stream Build(IReadOnlyList<string> pageFiles)
	{
		var path = Path.Combine(Path.GetTempPath(), $"sane-tui-{Guid.NewGuid():N}.pdf");
		var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.DeleteOnClose);

		try {
			Write(stream, pageFiles);
			stream.Position = 0;

			return stream;
		} catch {
			stream.Dispose();

			throw;
		}
	}

	private static void Write(FileStream output, IReadOnlyList<string> pageFiles)
	{
		// objects: 1 is the catalog, 2 the page list, then a page, its content and its picture for each page
		var offsets = new List<long>();

		void Text(string text) => output.Write(Encoding.ASCII.GetBytes(text));

		void Begin(int number)
		{
			offsets.Add(output.Position);
			Text($"{number} 0 obj\n");
		}

		Text("%PDF-1.4\n");
		output.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']); // high bytes in a comment mark the file as binary, as the spec asks

		Begin(1);
		Text("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

		Begin(2);
		Text($"<< /Type /Pages /Count {pageFiles.Count} /Kids [{string.Join(" ", pageFiles.Select((_, i) => $"{3 + i * 3} 0 R"))}] >>\nendobj\n");

		for (var i = 0; i < pageFiles.Count; i++) {
			var jpeg = JpegInfo.Read(pageFiles[i]);
			var width = Points(jpeg.Width, jpeg.DpiX);
			var height = Points(jpeg.Height, jpeg.DpiY);
			var content = $"q {width} 0 0 {height} 0 0 cm /Im0 Do Q";
			var first = 3 + i * 3;

			Begin(first);
			Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /XObject << /Im0 {first + 2} 0 R >> >> /Contents {first + 1} 0 R >>\nendobj\n");

			Begin(first + 1);
			Text($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

			Begin(first + 2);
			Text($"<< /Type /XObject /Subtype /Image /Width {jpeg.Width} /Height {jpeg.Height} /ColorSpace /{(jpeg.Components == 1 ? "DeviceGray" : "DeviceRGB")} /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");

			using (var file = File.OpenRead(pageFiles[i])) {
				file.CopyTo(output);
			}

			Text("\nendstream\nendobj\n");
		}

		var xref = output.Position;

		Text($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");

		foreach (var offset in offsets) {
			Text($"{offset:0000000000} 00000 n \n"); // exactly 20 bytes each, which is what a reader indexes by
		}

		Text($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
	}

	private static string Points(int pixels, double dpi) => (pixels * 72 / dpi).ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>What a PDF needs to know about a JPEG file, read from its headers.</summary>
internal sealed record JpegInfo(int Width, int Height, int Components, double DpiX, double DpiY, long Length)
{
	public static JpegInfo Read(string path)
	{
		var bytes = File.ReadAllBytes(path);

		if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) {
			throw new InvalidDataException($"{Path.GetFileName(path)} isn't a JPEG file.");
		}

		double dpiX = FallbackDpi, dpiY = FallbackDpi;

		for (var i = 2; i + 4 <= bytes.Length;) {
			if (bytes[i] != 0xFF) {
				throw new InvalidDataException($"{Path.GetFileName(path)} has a damaged JPEG header.");
			}

			var marker = bytes[i + 1];

			if (marker == 0xFF) {
				i++; // padding

				continue;
			}

			var length = bytes[i + 2] << 8 | bytes[i + 3];

			// APP0 "JFIF": units (1 = dots per inch, 2 = dots per centimeter), then the two densities
			if (marker == 0xE0 && length >= 16 && bytes.AsSpan(i + 4, 5).SequenceEqual("JFIF\0"u8) && bytes[i + 11] is 1 or 2) {
				var x = (bytes[i + 12] << 8) | bytes[i + 13];
				var y = (bytes[i + 14] << 8) | bytes[i + 15];

				if (x > 0 && y > 0) {
					var scale = bytes[i + 11] == 2 ? 2.54 : 1;

					dpiX = x * scale;
					dpiY = y * scale;
				}
			}

			// start of frame (any kind except the table definitions that share the numbers): precision, height, width, components
			if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC)) {
				var components = bytes[i + 9];

				if (components is not (1 or 3)) {
					throw new InvalidDataException($"{Path.GetFileName(path)} has {components} color components; only gray and RGB pictures are supported.");
				}

				return new JpegInfo(bytes[i + 7] << 8 | bytes[i + 8], bytes[i + 5] << 8 | bytes[i + 6], components, dpiX, dpiY, bytes.Length);
			}

			i += 2 + length;
		}

		throw new InvalidDataException($"{Path.GetFileName(path)} has no picture size in its JPEG header.");
	}

	private const double FallbackDpi = 150;
}
