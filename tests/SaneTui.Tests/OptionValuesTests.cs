using SaneTui.Sane;

namespace SaneTui.Tests;

public class OptionValuesTests
{
	private static SaneOption Option(SaneValueType type, SaneUnit unit = SaneUnit.None, SaneRange? range = null, double[]? numbers = null, string[]? strings = null) =>
		new(2, "thing", "Thing", "", type, unit, 4, SaneCapabilities.SoftSelect | SaneCapabilities.SoftDetect, null, range, numbers, strings);

	[Test]
	[Arguments("true", true)]
	[Arguments("YES", true)]
	[Arguments("on", true)]
	[Arguments("1", true)]
	[Arguments("false", false)]
	[Arguments("No", false)]
	[Arguments("0", false)]
	public async Task Bool_AcceptsCommonSpellings(string text, bool expected)
	{
		await Assert.That(OptionValues.Parse(Option(SaneValueType.Bool), text)).IsEqualTo(expected);
	}

	[Test]
	public async Task Bool_RejectsOtherText()
	{
		await Assert.That(() => OptionValues.Parse(Option(SaneValueType.Bool), "maybe")).Throws<CommandException>();
	}

	[Test]
	public async Task String_MatchesCaseInsensitively_AndReturnsTheDriversSpelling()
	{
		var option = Option(SaneValueType.String, strings: ["Gray", "Color"]);

		await Assert.That(OptionValues.Parse(option, "color")).IsEqualTo("Color");
	}

	[Test]
	public async Task String_NotInTheList_SaysWhatIsAllowed()
	{
		var option = Option(SaneValueType.String, strings: ["Gray", "Color"]);

		var ex = await Assert.ThrowsAsync<CommandException>(() => Task.FromResult(OptionValues.Parse(option, "purple")));

		await Assert.That(ex!.Message).Contains("Gray, Color");
	}

	[Test]
	public async Task String_WithoutAList_PassesThrough()
	{
		await Assert.That(OptionValues.Parse(Option(SaneValueType.String), "anything")).IsEqualTo("anything");
	}

	[Test]
	public async Task Number_InsideTheRange_IsAccepted()
	{
		var option = Option(SaneValueType.Int, SaneUnit.Dpi, range: new SaneRange(50, 1200, 0));

		await Assert.That(OptionValues.Parse(option, "300")).IsEqualTo(300d);
	}

	[Test]
	public async Task Number_OutsideTheRange_SaysTheRangeAndUnit()
	{
		var option = Option(SaneValueType.Int, SaneUnit.Dpi, range: new SaneRange(50, 1200, 0));

		var ex = await Assert.ThrowsAsync<CommandException>(() => Task.FromResult(OptionValues.Parse(option, "2400")));

		await Assert.That(ex!.Message).Contains("50 to 1200 dpi");
	}

	[Test]
	public async Task Number_NotInTheList_SaysWhatIsAllowed()
	{
		var option = Option(SaneValueType.Int, SaneUnit.Dpi, numbers: [100, 200, 300]);

		var ex = await Assert.ThrowsAsync<CommandException>(() => Task.FromResult(OptionValues.Parse(option, "250")));

		await Assert.That(ex!.Message).Contains("100, 200, 300 dpi");
	}

	[Test]
	public async Task IntOption_RejectsFractions()
	{
		await Assert.That(() => OptionValues.Parse(Option(SaneValueType.Int), "2.5")).Throws<CommandException>();
	}

	[Test]
	public async Task FixedOption_AcceptsFractions_InAnyCulture()
	{
		await Assert.That(OptionValues.Parse(Option(SaneValueType.Fixed), "2.5")).IsEqualTo(2.5);
	}

	[Test]
	public async Task NotANumber_IsRejected()
	{
		await Assert.That(() => OptionValues.Parse(Option(SaneValueType.Int), "lots")).Throws<CommandException>();
	}

	[Test]
	public async Task ButtonsAndGroups_HaveNoValueToSet()
	{
		await Assert.That(() => OptionValues.Parse(Option(SaneValueType.Button), "x")).Throws<CommandException>();
	}
}
