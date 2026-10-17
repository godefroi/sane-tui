# SaneTui.Sane

A .NET library for scanning with [SANE](http://www.sane-project.org/) on Linux. List scanners, read and change their options, scan a page or a whole document feeder, and get the pixels back.

It calls `libsane` directly and has no dependencies of its own. SANE has global state and its drivers aren't safe to call from several threads, so the library makes every call on one dedicated thread and gives you an async API on top of it.

This is the library under [sane-tui](https://github.com/godefroi/sane-tui), a terminal and web scanning app.

The API may still change before 1.0.

## What you need

- **Linux.** SANE is a Linux (and other Unix) framework; this library doesn't run on Windows or macOS.
- **.NET 10.**
- **SANE itself.** The native library (`libsane.so.1`: the `libsane1` package on Debian and Ubuntu, `sane-backends` on Fedora) and the driver for your scanner. None of that is in this package. Without the native library, `SaneLibrary.InitializeAsync` throws a `DllNotFoundException`.
- **Permission to open the scanner.** USB scanners are usually owned by a group (often `scanner`), so the user running your program has to be in it, or the host needs udev rules that grant access. In a container, pass the USB bus through (for example `--device /dev/bus/usb`) and make sure the container's user has the same permission.

You don't need a scanner to try things out: a standard SANE build includes a virtual device named `test`, so `sane.OpenAsync("test")` opens a scanner that produces a test picture.

## Install

```sh
dotnet add package SaneTui.Sane
```

## A first scan

```csharp
using SaneTui.Sane;

await using var sane = await SaneLibrary.InitializeAsync();

var scanners = await sane.GetDevicesAsync();

foreach (var scanner in scanners) {
	Console.WriteLine($"{scanner.Name}: {scanner.Vendor} {scanner.Model}");
}

await using var device = await sane.OpenAsync(scanners[0].Name);

await device.SetAsync(device.GetOption("resolution"), 300);
await device.SetScanAreaAsync(); // the largest area the scanner supports

SaneImage page = await device.ScanAsync();

Console.WriteLine($"{page.Width} x {page.Height} pixels, {page.Depth} bits per sample");
```

`GetDevicesAsync` asks the drivers to look for hardware, and network drivers look on the network, so it can take a few seconds. If you already know a scanner's name, `OpenAsync` takes it directly.

## Options

A scanner's settings are its options: resolution, color mode, scan area, and whatever a driver adds. Which exist, what they're called and which values they accept varies from driver to driver, so look at what the scanner offers before assuming.

```csharp
foreach (var option in device.Options.Where(o => o.IsSettable)) {
	Console.WriteLine($"{option.Name} ({option.Type}): {option.Value}");
}

var mode = device.GetOption("mode");

// the allowed values are in one of Range, NumberList or StringList, spelled the way the driver spells them
Console.WriteLine(string.Join(", ", mode.StringList ?? []));

await device.SetAsync(mode, "Color");
```

Setting an option can change others (switching to a document feeder changes which areas and resolutions are allowed), and the driver may adjust the value you gave it. `device.Options` is replaced after every change, so an option record you're holding is a snapshot: look it up again with `GetOption` to see the outcome.

`SetScanAreaAsync(width, height, x, y)` picks the part of the scanner to scan, in millimeters from the top-left corner. It works through the standard `tl-x`, `tl-y`, `br-x` and `br-y` options and cuts an area that is too big down to fit, so you can ask any scanner for a paper size. Set the source first, because the available area can depend on it. `SetAutoAsync` lets the driver choose a value, and `PressAsync` presses a button option.

## Document feeders

A feeder feeds sheets by itself once a job starts. Use `ScanBatchAsync`, which produces each page as it's scanned and ends when the feeder is empty:

```csharp
await device.SetAsync(device.GetOption("source"), "ADF Front"); // the spellings differ between drivers

var progress = new Progress<double>(fraction => Console.Write($"\r{fraction:P0}"));
using var cancel = new CancellationTokenSource();

await foreach (var sheet in device.ScanBatchAsync(progress, cancel.Token)) {
	// each sheet is yours as soon as it arrives
}
```

Calling `ScanAsync` once and stopping can use up the next sheet without ever scanning it, because the feeder has already pulled it. Don't use `ScanBatchAsync` on a flatbed: a flatbed never runs out of paper.

Progress goes from 0 to 1 for each page, on a background thread. Scanners warm up and calibrate before the first byte arrives, so a long wait at 0 isn't a stall. Cancelling the token stops a scan even while it's waiting on the hardware, which can take a moment, and leaves the device usable.

## Errors

A problem reported by the scanner is a `SaneException` with a `Status` you can act on:

```csharp
try {
	page = await device.ScanAsync();
} catch (SaneException ex) when (ex.Status == SaneStatus.NoDocs) {
	Console.WriteLine("The feeder is empty.");
} catch (SaneException ex) when (ex.Status is SaneStatus.Jammed or SaneStatus.CoverOpen) {
	Console.WriteLine($"The scanner needs attention: {ex.Message}");
}
```

Using the API wrongly is an `InvalidOperationException` or an `ArgumentException`: changing an option that is inactive or read-only, starting a second scan while one is running, or passing a value of the wrong type.

## The pixels

`SaneImage` is raw, uncompressed pixels: `Width`, `Height`, `Format` (gray, or interleaved RGB), `Depth` (1, 8 or 16 bits per sample) and `Data`. Turning it into a file is up to you. A few things to know when you do:

- Rows are `Stride` bytes apart, which can be more than the pixels in a row need.
- At 1 bit per sample a set bit is **black**, the opposite of 8-bit gray where 0 is black.
- 16-bit samples are in the machine's native byte order.

For 8-bit pictures, this writes a JPEG with [Magick.NET](https://github.com/dlemstra/Magick.NET):

```csharp
using ImageMagick;

var rgb = page.Format == SanePixelFormat.Rgb;
var rowBytes = page.Width * (rgb ? 3 : 1);
var tight = new byte[rowBytes * page.Height];

for (var y = 0; y < page.Height; y++) {
	page.Data.Span.Slice(y * page.Stride, rowBytes).CopyTo(tight.AsSpan(y * rowBytes));
}

using var image = new MagickImage();

image.ReadPixels(tight, new PixelReadSettings((uint)page.Width, (uint)page.Height, StorageType.Char, rgb ? "RGB" : "I"));
image.Write("page.jpg");
```

## Threading and lifetime

All calls into SANE happen on one background thread, and every `SaneLibrary` instance shares it. An instance is a cheap handle on that one session: SANE is started when the first is created and shut down when the last is disposed, which also closes any device still open. A device can do one thing at a time: options can't be changed during a scan, and a second scan can't start until the first finishes. Dispose a device when you're done with it, because a scanner generally can't be shared with other programs while it's open. Closing a scanner can take a while (it may park its scan head first).

## Links

- [Source and issues](https://github.com/godefroi/sane-tui)
- [Releases](https://github.com/godefroi/sane-tui/releases)
- [The SANE project](http://www.sane-project.org/), and its [API documentation](http://www.sane-project.org/html/)

Licensed under the Apache License 2.0.
