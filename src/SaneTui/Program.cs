using System.CommandLine;
using SaneTui;

var devicesArgument = new Argument<string[]>("device") {
	Description = "Devices to dump, e.g. 'test' or 'net:host:device'. Defaults to every device SANE can see.",
	Arity = ArgumentArity.ZeroOrMore,
};

var dump = new Command("dump", "Print every option of each device, to compare what different backends expose.") { devicesArgument };

dump.SetAction((parseResult, cancellationToken) => DumpCommand.RunAsync(parseResult.GetValue(devicesArgument) ?? [], cancellationToken));

var root = new RootCommand("Scan documents with SANE from the terminal. With no command, starts the interactive UI.") { dump };

root.SetAction(async (_, cancellationToken) => {
	await Tui.RunAsync(cancellationToken);

	return 0;
});

return await root.Parse(args).InvokeAsync();
