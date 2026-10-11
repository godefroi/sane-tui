using SaneTui.Sane;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SaneTui;

internal static class Tui
{
	// Stub: lists the devices SANE can see. Device picker, options and scanning come next.
	public static async Task RunAsync(CancellationToken cancellationToken)
	{
		await using var sane = await SaneLibrary.InitializeAsync();

		var devices = await sane.GetDevicesAsync();

		cancellationToken.ThrowIfCancellationRequested();

		using IApplication app = Application.Create();

		app.Init();

		using Window window = new() { Title = "sane-tui (Esc to quit)" };

		Label label = new() {
			Text = devices.Count == 0
				? "No scanners found."
				: string.Join('\n', devices.Select(d => $"{d.Vendor} {d.Model}  ({d.Name})")),
			X = Pos.Center(),
			Y = Pos.Center(),
		};

		window.Add(label);

		app.Run(window);
	}
}
