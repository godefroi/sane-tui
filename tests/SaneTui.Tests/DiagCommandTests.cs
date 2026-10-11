namespace SaneTui.Tests;

public class DiagCommandTests
{
	[Test]
	public async Task Guard_MissingLibrary_ReturnsExitCode2_InsteadOfThrowing()
	{
		var exitCode = await DiagCommand.GuardAsync(() => throw new DllNotFoundException("libsane.so.1 not found"));

		await Assert.That(exitCode).IsEqualTo(2);
	}

	[Test]
	public async Task Guard_PassesThroughTheCommandsExitCode()
	{
		await Assert.That(await DiagCommand.GuardAsync(() => Task.FromResult(7))).IsEqualTo(7);
	}
}
