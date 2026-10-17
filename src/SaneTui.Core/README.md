# SaneTui.Core

The scanning code that the [terminal program](../SaneTui/README.md) and the [web service](../SaneTui.Web/README.md) share, on top of the [`SaneTui.Sane`](../SaneTui.Sane/README.md) library. It is not published as a package, and its types are `internal`, visible only to the two front ends and the tests.

It has the parts that decide what a scan is, and none of the parts that draw it:

- **Scan requests and option setup:** `ScanRequest`, and `ScanSetup`, which applies a request to an open scanner in the right order (the source can change which modes and areas exist, and the mode can change which resolutions are allowed).
- **The preview scan:** `PreviewScan`, a quick whole-bed scan at low resolution, using a scanner's own speed-ups where it has them.
- **What to offer:** `ResolutionPresets` (the usual resolutions the scanner allows), `StringChoices` (the source and mode values worth offering), `PaperSizes`, and `FeederSources` (a guess from the source's name at whether it's a feeder, because the names differ between drivers).
- **Pages to files:** `ImageWriter` turns a scanned `SaneImage` into a JPEG with Magick.NET, applying `PageOptions` (rotate 180, deskew, trim) to the raw pixels first, and `OutputFiles` finds what is already in the way and the free names to use instead, so a file is only replaced when someone asks for that.
- **`OptionValues`:** parses `name=value` text into the right type for an option, with an error that says what's allowed.
