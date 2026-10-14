using System.Runtime.InteropServices;
using ImageMagick;
using SaneTui.Sane;

namespace SaneTui.Tests;

public class ImageWriterTests
{
	// JPEG is lossy, so these use large flat areas and compare brightness with generous margins
	private const int Dark = 6_000;
	private const int Light = 59_000;

	private static async Task<MagickImage> WriteAndReadBack(SaneImage image, double? dpi = null)
	{
		using var dir = new TempDirectory();
		var path = Path.Combine(dir.Path, "page.jpg");

		ImageWriter.WriteJpeg(image, dpi, path);

		// no temporary file may be left next to the result
		await Assert.That(Directory.GetFileSystemEntries(dir.Path).Select(p => Path.GetFileName(p)!)).IsEquivalentTo(["page.jpg"]);

		return new MagickImage(path);
	}

	private static int Brightness(MagickImage image, int x, int y, int channel = 0)
	{
		using var pixels = image.GetPixels();

		return pixels.GetPixel(x, y).GetChannel((uint)channel);
	}

	[Test]
	public async Task Gray8_RowPaddingIsNotPartOfTheImage()
	{
		// 10 white pixels per row, then 6 bytes of black padding to reach a 16-byte stride
		var data = new byte[16 * 16];

		for (var y = 0; y < 16; y++) {
			Array.Fill(data, (byte)255, y * 16, 10);
		}

		using var read = await WriteAndReadBack(new SaneImage(10, 16, SanePixelFormat.Gray, 8, 16, data));

		await Assert.That((int)read.Width).IsEqualTo(10);
		await Assert.That(Brightness(read, 9, 8)).IsGreaterThan(Light);
	}

	[Test]
	public async Task Lineart_SetBitsAreBlack()
	{
		// left 8 pixels set (black), right 8 clear (white); the opposite of 8-bit gray, where 0 is black
		var data = new byte[16 * 2];

		for (var y = 0; y < 16; y++) {
			data[y * 2] = 0xFF;
		}

		using var read = await WriteAndReadBack(new SaneImage(16, 16, SanePixelFormat.Gray, 1, 2, data));

		await Assert.That(Brightness(read, 3, 8)).IsLessThan(Dark);
		await Assert.That(Brightness(read, 12, 8)).IsGreaterThan(Light);
	}

	[Test]
	public async Task Lineart_RowsPaddedToWholeBytes_KeepTheirWidth()
	{
		// 12 pixels need 2 bytes per row; the 4 spare bits are set but must not widen the image
		var data = new byte[16 * 2];

		Array.Fill(data, (byte)0xFF);

		using var read = await WriteAndReadBack(new SaneImage(12, 16, SanePixelFormat.Gray, 1, 2, data));

		await Assert.That((int)read.Width).IsEqualTo(12);
		await Assert.That(Brightness(read, 11, 8)).IsLessThan(Dark);
	}

	[Test]
	public async Task Gray16_UsesNativeByteOrder()
	{
		// 0xC000 is 75% bright; if its two bytes were swapped it would read as nearly black
		var samples = new ushort[16 * 16];

		Array.Fill(samples, (ushort)0xC000);

		using var read = await WriteAndReadBack(new SaneImage(16, 16, SanePixelFormat.Gray, 16, 32, MemoryMarshal.AsBytes<ushort>(samples).ToArray()));

		await Assert.That(Brightness(read, 8, 8)).IsGreaterThan(40_000);
	}

	[Test]
	public async Task Rgb8_KeepsChannelsInOrder()
	{
		// left half red, right half blue
		var data = new byte[16 * 16 * 3];

		for (var y = 0; y < 16; y++) {
			for (var x = 0; x < 16; x++) {
				data[(y * 16 + x) * 3 + (x < 8 ? 0 : 2)] = 255;
			}
		}

		using var read = await WriteAndReadBack(new SaneImage(16, 16, SanePixelFormat.Rgb, 8, 48, data));

		await Assert.That(Brightness(read, 3, 8, channel: 0)).IsGreaterThan(Light);
		await Assert.That(Brightness(read, 3, 8, channel: 2)).IsLessThan(Dark);
		await Assert.That(Brightness(read, 12, 8, channel: 0)).IsLessThan(Dark);
		await Assert.That(Brightness(read, 12, 8, channel: 2)).IsGreaterThan(Light);
	}

	[Test]
	public async Task Resolution_IsRecordedSoTheFileHasAPhysicalSize()
	{
		var image = new SaneImage(16, 16, SanePixelFormat.Gray, 8, 16, new byte[16 * 16]);

		using var read = await WriteAndReadBack(image, dpi: 300);

		await Assert.That(read.Density.X).IsEqualTo(300d);
		await Assert.That(read.Density.Units).IsEqualTo(DensityUnit.PixelsPerInch);
	}

	private static SaneImage Blank(byte value) => new(16, 16, SanePixelFormat.Gray, 8, 16, Enumerable.Repeat(value, 16 * 16).ToArray());

	[Test]
	public async Task ExistingFile_IsNeverReplaced_TheImageGetsAFreeNameInstead()
	{
		using var dir = new TempDirectory();
		var path = Path.Combine(dir.Path, "page.jpg");

		File.WriteAllText(path, "something precious");

		var written = ImageWriter.WriteJpeg(Blank(200), null, path);

		await Assert.That(File.ReadAllText(path)).IsEqualTo("something precious");
		await Assert.That(Path.GetFileName(written)).IsEqualTo("page (2).jpg");
		await Assert.That(new FileInfo(written).Length).IsGreaterThan(100);
	}

	[Test]
	public async Task SeveralWritesToOneName_AllSurvive()
	{
		using var dir = new TempDirectory();
		var path = Path.Combine(dir.Path, "page.jpg");

		var names = new[] { ImageWriter.WriteJpeg(Blank(10), null, path), ImageWriter.WriteJpeg(Blank(100), null, path), ImageWriter.WriteJpeg(Blank(250), null, path) };

		await Assert.That(names.Select(p => Path.GetFileName(p)!)).IsEquivalentTo(["page.jpg", "page (2).jpg", "page (3).jpg"]);
		await Assert.That(Directory.GetFiles(dir.Path).Length).IsEqualTo(3); // and no temporary files left over
	}

	[Test]
	public async Task Overwrite_ReplacesTheFile_WhenAskedToExplicitly()
	{
		using var dir = new TempDirectory();
		var path = Path.Combine(dir.Path, "page.jpg");

		File.WriteAllText(path, "old");

		var written = ImageWriter.WriteJpeg(Blank(200), null, path, overwrite: true);

		await Assert.That(written).IsEqualTo(path);
		await Assert.That(Directory.GetFiles(dir.Path).Length).IsEqualTo(1);
		await Assert.That(new FileInfo(path).Length).IsGreaterThan(100); // a JPEG now, not the three bytes of "old"
	}

	[Test]
	[Arguments("scan.jpg", true)]
	[Arguments("SCAN.JPEG", true)]
	[Arguments("scan.png", false)]
	[Arguments("scan", false)]
	public async Task IsJpeg_GoesByExtension(string path, bool expected)
	{
		await Assert.That(ImageWriter.IsJpeg(path)).IsEqualTo(expected);
	}

	[Test]
	public async Task Rotate180_TurnsThePageHalfWayRound()
	{
		// dark on top, light below
		var data = new byte[16 * 16];

		Array.Fill(data, (byte)255, 16 * 8, 16 * 8);

		using var dir = new TempDirectory();
		var path = Path.Combine(dir.Path, "page.jpg");

		ImageWriter.WriteJpeg(new SaneImage(16, 16, SanePixelFormat.Gray, 8, 16, data), null, path, rotate180: true);

		using var read = new MagickImage(path);

		await Assert.That(Brightness(read, 8, 3)).IsGreaterThan(Light);
		await Assert.That(Brightness(read, 8, 12)).IsLessThan(Dark);
	}
}

internal sealed class TempDirectory : IDisposable
{
	public string Path { get; } = Directory.CreateTempSubdirectory("sanetui-test-").FullName;

	public void Dispose() => Directory.Delete(Path, recursive: true);
}
