using ImageMagick;
using SaneTui.Sane;

namespace SaneTui;

/// <summary>Encodes scanned pages as image files.</summary>
internal static class ImageWriter
{
	private const int JpegQuality = 90; // ImageMagick's default of 75 shows visible artifacts around text

	public static bool IsJpeg(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg";

	/// <summary>
	/// Writes <paramref name="image"/> to <paramref name="path"/> as a JPEG. The file appears complete or not at all:
	/// it's written under a temporary name and renamed, so an interrupted scan can't leave a half-written file behind.
	/// </summary>
	/// <param name="dpi">The scan resolution, so viewers and printers know the physical size. Null if unknown.</param>
	public static void WriteJpeg(SaneImage image, double? dpi, string path)
	{
		using var magick = ToMagick(image);

		if (dpi is { } d) {
			magick.Density = new Density(d, DensityUnit.PixelsPerInch);
		}

		magick.Quality = JpegQuality;

		var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

		try {
			magick.Write(temp, MagickFormat.Jpeg);
			File.Move(temp, path, overwrite: true);
		} catch {
			// best effort: a failure to clean up (say, the folder doesn't exist) must not hide the error that got us here
			try {
				File.Delete(temp);
			} catch (IOException) {
			} catch (UnauthorizedAccessException) {
			}

			throw;
		}
	}

	internal static MagickImage ToMagick(SaneImage image)
	{
		var rgb = image.Format == SanePixelFormat.Rgb;
		var (pixels, storage) = Pack(image);
		var magick = new MagickImage();

		magick.ReadPixels(pixels, new PixelReadSettings((uint)image.Width, (uint)image.Height, storage, rgb ? "RGB" : "I"));

		if (!rgb) {
			magick.ColorType = ColorType.Grayscale; // a gray JPEG is a third the size of an RGB one holding the same pixels
		}

		return magick;
	}

	// ImageMagick wants rows back to back, with no padding, and 8 or 16 bits per sample
	private static (byte[] Pixels, StorageType Storage) Pack(SaneImage image)
	{
		var data = image.Data.Span;

		if (image.Depth == 1) {
			// 1 bit per pixel, and a set bit is black: opposite to 8-bit gray, where 0 is black
			var expanded = new byte[image.Width * image.Height];

			for (var y = 0; y < image.Height; y++) {
				for (var x = 0; x < image.Width; x++) {
					var set = (data[y * image.Stride + x / 8] >> (7 - x % 8) & 1) != 0;

					expanded[y * image.Width + x] = set ? (byte)0 : (byte)255;
				}
			}

			return (expanded, StorageType.Char);
		}

		var rowBytes = image.Width * (image.Format == SanePixelFormat.Rgb ? 3 : 1) * (image.Depth / 8);

		if (image.Stride == rowBytes) {
			return (data.Slice(0, rowBytes * image.Height).ToArray(), Storage(image.Depth));
		}

		var packed = new byte[rowBytes * image.Height];

		for (var y = 0; y < image.Height; y++) {
			data.Slice(y * image.Stride, rowBytes).CopyTo(packed.AsSpan(y * rowBytes));
		}

		return (packed, Storage(image.Depth));
	}

	// 16-bit samples arrive in the machine's byte order, which is what ImageMagick reads
	private static StorageType Storage(int depth) => depth == 16 ? StorageType.Short : StorageType.Char;
}
