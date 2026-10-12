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

var scanDevice = new Argument<string?>("device") {
	Description = "The scanner to use, as listed by 'sane-tui diag'. Optional if there is only one.",
	Arity = ArgumentArity.ZeroOrOne,
};
var scanOutput = new Option<string>("--output", "-o") { Description = "The JPEG file to write (.jpg). With --batch, the page number is added to the name: scan-001.jpg, scan-002.jpg, ...", Required = true };
var scanSource = new Option<string?>("--source") { Description = "Where the paper is: for example a flatbed or document feeder. The names depend on the scanner; 'sane-tui dump' lists them." };
var scanMode = new Option<string?>("--mode") { Description = "Color, Gray or Lineart (as the scanner names them)." };
var scanDepth = new Option<int?>("--depth") { Description = "Bits per sample, usually 8." };
var scanResolution = new Option<double>("--resolution") { Description = "Dots per inch. Scanners often default to a low value, so this defaults to 300.", DefaultValueFactory = _ => 300 };
var scanWidth = new Option<double?>("--width") { Description = "Width of the area to scan in millimeters. Default: the full width of the scanner." };
var scanHeight = new Option<double?>("--height") { Description = "Height of the area to scan in millimeters. Default: the full height of the scanner." };
var scanBatch = new Option<bool>("--batch") { Description = "Scan every sheet in the document feeder. Don't use this with a flatbed, which never runs out of pages (Ctrl+C or --max-pages stops it)." };
var scanMaxPages = new Option<int?>("--max-pages") { Description = "Stop a --batch after this many pages." };
var scanSet = new Option<string[]>("--set") { Description = "Set any other scanner option, as name=value. Can be repeated. Applied last.", Arity = ArgumentArity.ZeroOrMore };

var scan = new Command("scan", "Scan a page, or everything in the document feeder, to JPEG files.") {
	scanDevice, scanOutput, scanSource, scanMode, scanDepth, scanResolution, scanWidth, scanHeight, scanBatch, scanMaxPages, scanSet,
};

scan.SetAction((parseResult, cancellationToken) => DiagCommand.GuardAsync(() => ScanCommand.RunAsync(
	new ScanRequest(
		parseResult.GetValue(scanDevice),
		parseResult.GetRequiredValue(scanOutput),
		parseResult.GetValue(scanSource),
		parseResult.GetValue(scanMode),
		parseResult.GetValue(scanDepth),
		parseResult.GetValue(scanResolution),
		parseResult.GetValue(scanWidth),
		parseResult.GetValue(scanHeight),
		parseResult.GetValue(scanBatch),
		parseResult.GetValue(scanMaxPages),
		parseResult.GetValue(scanSet) ?? []),
	Console.Error,
	cancellationToken)));

var root = new RootCommand("Scan documents with SANE from the terminal. With no command, starts the interactive UI.") { diag, dump, scan };

root.SetAction((_, cancellationToken) => DiagCommand.GuardAsync(async () => {
	await Tui.RunAsync(cancellationToken);

	return 0;
}));

return await root.Parse(args).InvokeAsync();
