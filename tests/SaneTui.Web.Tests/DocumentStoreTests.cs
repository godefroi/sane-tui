using Microsoft.Extensions.Options;
using SaneTui.Sane;

namespace SaneTui.Web.Tests;

public class DocumentStoreTests
{
	// a gray picture with a gradient, so a JPEG of it isn't trivially tiny
	private static SaneImage Picture(int width = 64, int height = 48, int seed = 0)
	{
		var data = new byte[width * height];

		for (var y = 0; y < height; y++) {
			for (var x = 0; x < width; x++) {
				data[y * width + x] = (byte)((x * 3 + y * 5 + seed * 40) % 256);
			}
		}

		return new SaneImage(width, height, SanePixelFormat.Gray, 8, width, data);
	}

	private static DocumentStore Store(string folder) => new(Options.Create(new WebOptions { DataFolder = folder }));

	[Test]
	public async Task Pages_AreKeptInOrder_AndSurviveARestart()
	{
		using var dir = new TempFolder();
		var store = Store(dir.Path);

		store.AddPage(Picture(seed: 0), 150, default);
		store.AddPage(Picture(seed: 1), 150, default);

		var again = Store(dir.Path);

		await Assert.That(again.Current!.Pages.Select(p => p.Id)).IsEquivalentTo(["p0001", "p0002"]);
		await Assert.That(again.Current.Id).IsEqualTo(store.Current!.Id);
		await Assert.That(File.Exists(again.PagePath("p0002")!)).IsTrue();
	}

	[Test]
	public async Task ADeletedPage_IsGone_AndItsNumberIsNotReused()
	{
		using var dir = new TempFolder();
		var store = Store(dir.Path);

		store.AddPage(Picture(), 150, default);
		store.AddPage(Picture(), 150, default);

		var path = store.PagePath("p0001")!;

		await Assert.That(store.DeletePage("p0001")).IsTrue();
		await Assert.That(File.Exists(path)).IsFalse();
		await Assert.That(store.DeletePage("p0001")).IsFalse();

		var next = store.AddPage(Picture(), 150, default);

		await Assert.That(next.Id).IsEqualTo("p0003"); // a cached thumbnail of p0001 can't be mistaken for a new page
		await Assert.That(store.Current!.Pages.Select(p => p.Id)).IsEquivalentTo(["p0002", "p0003"]);
	}

	[Test]
	[Arguments("../../etc/passwd")]
	[Arguments("p0001/../../x")]
	[Arguments("p0001.jpg")]
	[Arguments("p9999")]
	[Arguments("")]
	public async Task APageIdThatIsNotAPageOfTheDocument_HasNoFile(string id)
	{
		using var dir = new TempFolder();
		var store = Store(dir.Path);

		store.AddPage(Picture(), 150, default);

		await Assert.That(store.PagePath(id)).IsNull();
	}

	[Test]
	public async Task Discarding_RemovesTheDocumentAndItsFiles()
	{
		using var dir = new TempFolder();
		var store = Store(dir.Path);

		store.AddPage(Picture(), 150, default);

		var path = store.PagePath("p0001")!;

		store.Discard();

		await Assert.That(store.Current).IsNull();
		await Assert.That(File.Exists(path)).IsFalse();
		await Assert.That(Store(dir.Path).Current).IsNull();
	}

	[Test]
	public async Task APdf_HoldsEachJpegPageUnchanged()
	{
		using var dir = new TempFolder();
		var store = Store(dir.Path);

		store.AddPage(Picture(seed: 0), 150, default);
		store.AddPage(Picture(seed: 2), 150, default);

		await using var pdf = PdfBuilder.Build(store.PagePaths());
		using var copy = new MemoryStream();

		await pdf.CopyToAsync(copy);

		var bytes = copy.ToArray();

		await Assert.That(bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8)).IsTrue();

		// the pictures' own bytes are inside the PDF: nothing was compressed a second time
		foreach (var page in store.PagePaths()) {
			await Assert.That(bytes.AsSpan().IndexOf(File.ReadAllBytes(page))).IsGreaterThanOrEqualTo(0);
		}
	}
}

internal sealed class TempFolder : IDisposable
{
	public string Path { get; } = Directory.CreateTempSubdirectory("sanetui-web-test-").FullName;

	public void Dispose() => Directory.Delete(Path, recursive: true);
}
