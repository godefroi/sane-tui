using SaneTui.Sane;

namespace SaneTui.Tests;

public class ResolutionPresetsTests
{
	private static SaneOption Resolution(SaneRange? range = null, double[]? list = null) =>
		new(5, "resolution", "Scan resolution", "", SaneValueType.Int, SaneUnit.Dpi, 4, SaneCapabilities.SoftSelect | SaneCapabilities.SoftDetect, 50, range, list, null);

	[Test]
	public async Task ContinuousRange_OffersEveryCommonResolutionInside()
	{
		// a flatbed that allows 50 to 1200 in any increment
		var choices = ResolutionPresets.For(Resolution(range: new SaneRange(50, 1200, 0)));

		await Assert.That(choices).IsEquivalentTo([50d, 75, 100, 150, 200, 240, 300, 360, 400, 600, 1200]);
	}

	[Test]
	public async Task ListOfAllowedValues_OffersOnlyTheCommonOnesItHas()
	{
		// a document feeder with a fixed list: 1200 isn't offered because it isn't supported
		var choices = ResolutionPresets.For(Resolution(list: [50, 75, 100, 150, 200, 240, 300, 360, 400, 600]));

		await Assert.That(choices).IsEquivalentTo([50d, 75, 100, 150, 200, 240, 300, 360, 400, 600]);
	}

	[Test]
	public async Task RangeWithAStep_OffersOnlyValuesOnTheStep()
	{
		// 100 to 600 in steps of 100: 150 isn't on a step
		var choices = ResolutionPresets.For(Resolution(range: new SaneRange(100, 600, 100)));

		await Assert.That(choices).IsEquivalentTo([100d, 200, 300, 400, 600]);
	}

	[Test]
	public async Task NothingInCommon_FallsBackToTheDevicesOwnValues_SoThereIsAlwaysAChoice()
	{
		await Assert.That(ResolutionPresets.For(Resolution(list: [111, 222]))).IsEquivalentTo([111d, 222]);
		await Assert.That(ResolutionPresets.For(Resolution(range: new SaneRange(7, 9, 0)))).IsEquivalentTo([7d, 9]);
	}

	[Test]
	public async Task NoConstraint_OrNoOption_OffersTheCommonList()
	{
		await Assert.That(ResolutionPresets.For(Resolution())).Contains(300d);
		await Assert.That(ResolutionPresets.For(null)).Contains(300d);
	}

	[Test]
	public async Task Default_IsTheChoiceClosestTo300()
	{
		await Assert.That(ResolutionPresets.Default([50, 75, 100, 150, 200, 240, 300])).IsEqualTo(300d);
		await Assert.That(ResolutionPresets.Default([50, 100, 150])).IsEqualTo(150d);
		await Assert.That(ResolutionPresets.Default([600, 1200])).IsEqualTo(600d);
	}
}

public class StatusWriterTests
{
	[Test]
	public async Task WritesEachFinishedLineToTheCallback()
	{
		var lines = new List<string>();
		var writer = new StatusWriter(lines.Add);

		writer.WriteLine("first");
		writer.Write("sec");
		writer.Write("ond");
		writer.WriteLine();

		await Assert.That(lines).IsEquivalentTo(["first", "second"]);
	}

	[Test]
	public async Task DropsCarriageReturns_AndHoldsAnUnfinishedLine()
	{
		var lines = new List<string>();
		var writer = new StatusWriter(lines.Add);

		writer.Write("a\r\nb"); // "b" isn't finished yet

		await Assert.That(lines).IsEquivalentTo(["a"]);
	}
}
