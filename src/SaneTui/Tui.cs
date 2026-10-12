using SaneTui.Sane;
using Terminal.Gui.App;

namespace SaneTui;

internal static class Tui
{
	public static async Task RunAsync(CancellationToken cancellationToken)
	{
		var sane = await SaneLibrary.InitializeAsync();

		try {
			using IApplication app = Application.Create();

			app.Init();

			using var window = new ScanWindow(app, sane);

			app.Run(window);

			// a scan still stopping when the window closed
			await window.Finished;
		} finally {
			// the terminal is restored by now, so this can talk to the user; some scanners take a while to park their head
			await ScanCommand.Patiently("Shutting down SANE. The scanner may still be returning the scan head and switching its lamp off...", sane.DisposeAsync, Console.Error);
		}
	}
}
