namespace SaneTui.Tests;

public class FeederSourcesTests
{
	[Test]
	[Arguments("ADF Front")]
	[Arguments("ADF Duplex")]
	[Arguments("Automatic Document Feeder")]
	[Arguments("automatic document feeder")]
	[Arguments("adf")]
	public async Task FeederNames_AreRecognised(string source)
	{
		await Assert.That(FeederSources.Looks(source)).IsTrue();
	}

	[Test]
	[Arguments("Flatbed")]
	[Arguments("Normal")]
	[Arguments("Transparency")]
	[Arguments("Negative")]
	[Arguments("")]
	public async Task OtherSources_AreNot(string source)
	{
		await Assert.That(FeederSources.Looks(source)).IsFalse();
	}

	[Test]
	public async Task NoSource_IsNotAFeeder()
	{
		await Assert.That(FeederSources.Looks(null)).IsFalse();
	}
}
