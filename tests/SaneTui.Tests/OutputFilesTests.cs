namespace SaneTui.Tests;

public class OutputFilesTests
{
	private static string Touch(TempDirectory dir, string name, string content = "original")
	{
		var path = Path.Combine(dir.Path, name);

		File.WriteAllText(path, content);

		return path;
	}

	[Test]
	public async Task Single_ExistingFile_IsFound_AndAMissingOneIsNot()
	{
		using var dir = new TempDirectory();
		var output = Path.Combine(dir.Path, "scan.jpg");

		await Assert.That(OutputFiles.Existing(output, batch: false)).IsEmpty();

		Touch(dir, "scan.jpg");

		await Assert.That(OutputFiles.Existing(output, batch: false)).IsEquivalentTo([output]);
	}

	[Test]
	public async Task Batch_AnyNumberedPageIsInTheWay_NotJustTheFirst()
	{
		using var dir = new TempDirectory();
		var output = Path.Combine(dir.Path, "scan.jpg");

		// how many pages a batch will have isn't known beforehand, so a lone leftover page 3 still counts
		Touch(dir, "scan-003.jpg");
		Touch(dir, "scan-004.jpg");

		var existing = OutputFiles.Existing(output, batch: true);

		await Assert.That(existing.Select(p => Path.GetFileName(p)!)).IsEquivalentTo(["scan-003.jpg", "scan-004.jpg"]);
	}

	[Test]
	public async Task Batch_IgnoresFilesThatOnlyLookSimilar()
	{
		using var dir = new TempDirectory();

		Touch(dir, "scan.jpg"); // the single-page name isn't a batch page
		Touch(dir, "scan-notes.jpg");
		Touch(dir, "scan-001.png"); // a different extension
		Touch(dir, "scan-001.jpg.bak");
		Touch(dir, "other-001.jpg");

		await Assert.That(OutputFiles.Existing(Path.Combine(dir.Path, "scan.jpg"), batch: true)).IsEmpty();
	}

	[Test]
	public async Task FreeName_Single_AddsANumber_SkippingTakenOnes()
	{
		using var dir = new TempDirectory();
		var output = Path.Combine(dir.Path, "scan.jpg");

		await Assert.That(OutputFiles.FreeName(output, batch: false)).IsEqualTo(output); // already free

		Touch(dir, "scan.jpg");

		await Assert.That(Path.GetFileName(OutputFiles.FreeName(output, batch: false))).IsEqualTo("scan (2).jpg");

		Touch(dir, "scan (2).jpg");

		await Assert.That(Path.GetFileName(OutputFiles.FreeName(output, batch: false))).IsEqualTo("scan (3).jpg");
	}

	[Test]
	public async Task FreeName_Batch_MovesTheWholeSeriesTogether()
	{
		using var dir = new TempDirectory();
		var output = Path.Combine(dir.Path, "scan.jpg");

		Touch(dir, "scan-001.jpg");
		Touch(dir, "scan (2)-002.jpg"); // an older series already used the (2) name, even though page 1 of it is gone

		// the pages of one batch share a name, so the next series is (3), not a mix of (2) and (3)
		await Assert.That(Path.GetFileName(OutputFiles.FreeName(output, batch: true))).IsEqualTo("scan (3).jpg");
	}

	[Test]
	public async Task Describe_NamesTheFile_AndCountsTheRestOfABatch()
	{
		await Assert.That(OutputFiles.Describe(["/x/scan.jpg"], batch: false)).IsEqualTo("scan.jpg already exists.");
		await Assert.That(OutputFiles.Describe(["/x/scan-001.jpg"], batch: true)).IsEqualTo("scan-001.jpg already exists.");
		await Assert.That(OutputFiles.Describe(["/x/scan-001.jpg", "/x/scan-002.jpg", "/x/scan-003.jpg"], batch: true)).IsEqualTo("scan-001.jpg and 2 other numbered file(s) already exist.");
	}
}
