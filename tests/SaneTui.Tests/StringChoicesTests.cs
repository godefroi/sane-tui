using SaneTui.Sane;

namespace SaneTui.Tests;

public class StringChoicesTests
{
	private static SaneOption Option(SaneCapabilities caps, string? value, params string[]? choices) =>
		new(2, "source", "Scan source", "", SaneValueType.String, SaneUnit.None, 16, caps, value, null, null, choices);

	private const SaneCapabilities Settable = SaneCapabilities.SoftSelect | SaneCapabilities.SoftDetect;

	[Test]
	public async Task ActiveOption_OffersItsChoices_WithTheCurrentValueSelected()
	{
		var offered = StringChoices.For(Option(Settable, "ADF Duplex", "ADF Front", "ADF Duplex"));

		await Assert.That(offered).IsNotNull();
		await Assert.That(offered!.Value.Choices).IsEquivalentTo(["ADF Front", "ADF Duplex"]);
		await Assert.That(offered.Value.Selected).IsEqualTo("ADF Duplex");
	}

	[Test]
	public async Task InactiveOption_IsNotOffered_SoItIsNeverSetAndRefused()
	{
		// a flatbed lists Normal / Transparency / Negative but marks the option inactive until an adapter is attached
		var offered = StringChoices.For(Option(Settable | SaneCapabilities.Inactive, null, "Normal", "Transparency", "Negative"));

		await Assert.That(offered).IsNull();
	}

	[Test]
	public async Task OptionOnlyTheUserCanSet_IsNotOffered()
	{
		// settable with a physical switch on the scanner, not from software
		var offered = StringChoices.For(Option(SaneCapabilities.HardSelect | SaneCapabilities.SoftDetect, "A", "A", "B"));

		await Assert.That(offered).IsNull();
	}

	[Test]
	public async Task CurrentValueTheScannerDoesNotList_FallsBackToTheFirstChoice()
	{
		var offered = StringChoices.For(Option(Settable, "something else", "Gray", "Color"));

		await Assert.That(offered!.Value.Selected).IsEqualTo("Gray");
	}

	[Test]
	public async Task NoValueReported_SelectsTheFirstChoice()
	{
		var offered = StringChoices.For(Option(Settable, null, "Lineart", "Gray", "Color"));

		await Assert.That(offered!.Value.Selected).IsEqualTo("Lineart");
	}

	[Test]
	public async Task NoListOfChoices_OrNoOption_IsNotOffered()
	{
		await Assert.That(StringChoices.For(Option(Settable, "x"))).IsNull();
		await Assert.That(StringChoices.For(null)).IsNull();
	}
}
