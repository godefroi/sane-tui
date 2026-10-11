using System.CommandLine;
using SaneTui;

var devicesArgument = new Argument<string[]>("device") {
	Description = "Devices to dump, e.g. 'test' or 'net:host:device'. Defaults to every device SANE can see.",
	Arity = ArgumentArity.ZeroOrMore,
};

var dump = new Command("dump", "Print every option of each device, to compare what different backends expose.") { devicesArgument };

dump.SetAction((parseResult, cancellationToken) => DiagCommand.GuardAsync(() => DumpCommand.RunAsync(parseResult.GetValue(devicesArgument) ?? [], cancellationToken)));

var diag = new Command("diag", "Check that libsane loads, list the scanners SANE finds, and point out USB devices that look like scanners but weren't claimed.");

diag.SetAction(_ => DiagCommand.RunAsync());

var root = new RootCommand("Scan documents with SANE from the terminal. With no command, starts the interactive UI.") { diag, dump };

root.SetAction((_, cancellationToken) => DiagCommand.GuardAsync(async () => {
	await Tui.RunAsync(cancellationToken);

	return 0;
}));

return await root.Parse(args).InvokeAsync();
