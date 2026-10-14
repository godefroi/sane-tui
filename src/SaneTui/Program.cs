using System.CommandLine;
using System.Diagnostics;
using SaneTui;

// The first Ctrl+C asks the running command to stop, which it does at the next safe point. SANE calls can't be interrupted
// (closing a scanner may wait for the scan head to park), so a second Ctrl+C ends the process outright instead of leaving it unkillable.
var interrupts = 0;

Console.CancelKeyPress += (_, _) => {
	if (Interlocked.Increment(ref interrupts) > 1) {
		Console.Error.WriteLine("Interrupted again. Exiting without waiting for the scanner.");
		Process.GetCurrentProcess().Kill();
	}
};

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
var scanPaper = new Option<string?>("--paper") { Description = $"The size of the page: {string.Join(", ", PaperSizes.Names)}. A scanner that can't cover it scans what it can and says so. Default: the full area of the scanner." };
var scanWidth = new Option<double?>("--width") { Description = "Width of the area to scan in millimeters, instead of --paper. Default: the full width of the scanner." };
var scanHeight = new Option<double?>("--height") { Description = "Height of the area to scan in millimeters, instead of --paper. Default: the full height of the scanner." };
var scanBatch = new Option<bool>("--batch") { Description = "Scan every sheet in the document feeder. Use this for a feeder: it feeds sheets automatically, so stopping after one can use up the next. Don't use it with a flatbed, which never runs out of pages (Ctrl+C or --max-pages stops it)." };
var scanMaxPages = new Option<int?>("--max-pages") { Description = "Stop a --batch after this many pages." };
var scanIfExists = new Option<ExistingFiles>("--if-exists") {
	Description = "What to do if the output file already exists: fail (the default: scan nothing), overwrite (replace it), or rename (keep it and save under a new name such as scan (2).jpg). Existing files are never replaced unless you say overwrite.",
	DefaultValueFactory = _ => ExistingFiles.Fail,
};
var scanRotate = new Option<bool>("--rotate-180") { Description = "Turn each saved page half way round, for a page laid on the scanner upside-down." };
var scanSet = new Option<string[]>("--set") { Description = "Set any other scanner option, as name=value. Can be repeated. Applied last.", Arity = ArgumentArity.ZeroOrMore };

var scan = new Command("scan", "Scan a page, or everything in the document feeder, to JPEG files.") {
	scanDevice, scanOutput, scanSource, scanMode, scanDepth, scanResolution, scanPaper, scanWidth, scanHeight, scanBatch, scanMaxPages, scanIfExists, scanRotate, scanSet,
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
		parseResult.GetValue(scanSet) ?? [],
		parseResult.GetValue(scanPaper),
		parseResult.GetValue(scanIfExists),
		Rotate180: parseResult.GetValue(scanRotate)),
	Console.Error,
	cancellationToken,
	showProgress: !Console.IsErrorRedirected)));

var root = new RootCommand("Scan documents with SANE from the terminal. With no command, starts the interactive UI.") { diag, dump, scan };

root.SetAction((_, cancellationToken) => DiagCommand.GuardAsync(async () => {
	await Tui.RunAsync(cancellationToken);

	return 0;
}));

return await root.Parse(args).InvokeAsync();
