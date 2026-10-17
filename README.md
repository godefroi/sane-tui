# sane-tui

A terminal UI for scanning documents on Linux (think Skanlite or SimpleScan, but in the terminal), built on a .NET library that talks to [SANE](http://www.sane-project.org/) directly. The library is meant to be useful on its own.

Status: early. The library can enumerate devices, open them, read and set options, and scan pages (single, or a whole document feeder). The terminal UI can pick a scanner, preview the bed of a flatbed and drag out the area to scan, and scan the whole feeder of a document scanner.

## Layout

| Path | What |
|------|------|
| `src/SaneTui.Sane` | The SANE library (`SaneLibrary`, `SaneDevice`, `SaneOption`). P/Invoke to `libsane.so.1`, no other dependencies. |
| `src/SaneTui.Core` | What the front ends share on top of the library: scan requests and option setup, the preview scan, JPEG writing (Magick.NET), page sizes, resolution presets, output-file naming. |
| `src/SaneTui` | The `sane-tui` executable: a Terminal.Gui interface (the default command), and the `diag`, `scan` and `dump` commands. |
| `src/SaneTui.Web` | The web service and its single page: the same scanning in a browser, for a scanner on a server. |
| `tests/SaneTui.Sane.Tests` | TUnit tests (Microsoft.Testing.Platform) against SANE's virtual `test` backend. |
| `tests/SaneTui.Tests`, `tests/SaneTui.Web.Tests` | Tests for the front ends; the web ones start the service in-process. |

Targets .NET 10. Linux only, since SANE is.

## Requirements

`libsane1` (Debian/Ubuntu) or your distro's equivalent, plus the backend for your scanner.

## Usage

```sh
dotnet run --project src/SaneTui                 # interactive UI: pick a scanner, settings and a page size, preview a flatbed, then Scan
dotnet run --project src/SaneTui -- diag         # does libsane load? which scanners does SANE find?
dotnet run --project src/SaneTui -- scan -o page.jpg          # scan with the only scanner, at 300 dpi, full area
dotnet run --project src/SaneTui -- dump         # print every option of every device SANE can see
```

- `diag` is the place to start when nothing works. It reports which libsane was loaded (or why it couldn't be), lists the scanners SANE found, and points out USB devices that look like scanners but weren't claimed by any driver, with a check of whether you may open them.
- `scan` writes JPEG files. Pick a scanner by name (from `diag`) if there's more than one. Common settings have flags (`--source`, `--mode`, `--resolution`, and `--paper a4|a5|a6|letter|legal` or `--width`/`--height` in millimeters); anything else is `--set name=value`. It says what area and resolution it's scanning, and on a terminal it shows progress. A scanner that can't cover the paper size scans what it can and tells you. Values are checked against what the scanner allows, and the error says what it does allow. Existing files are never replaced unasked: `--if-exists fail` (the default) refuses before scanning, `overwrite` replaces, and `rename` keeps the old files and saves under new names such as `scan (2).jpg`. (The terminal UI asks: Yes, No or Cancel.) `--batch` scans a whole document feeder to `page-001.jpg`, `page-002.jpg`, and so on. Use it for feeders: a feeder feeds sheets by itself, so stopping after one can use up the next. (The terminal UI does this for you: it offers "All pages in the feeder", ticked by default, when the source looks like a feeder.) Pages already scanned are kept if a later one fails.
- In the UI, pages appear in the preview pane as they are scanned (PageUp and PageDown step through the pages of the latest scan, and back to the bed preview); the list starts over each time Scan is pressed. A flatbed's Preview button scans the whole bed. Drag over the picture to choose the area (it snaps to terminal cells), then adjust it from the keyboard: arrow keys slide it 1 mm, Shift+arrows move its right and bottom edges. "Rotate 180°" (`--rotate-180` for `scan`) turns the preview and the saved page half way round, for a page laid on the glass upside-down. "Deskew" (`--deskew`) straightens a crooked page and "Trim" (`--trim`) cuts the background away, both done on the scanner's own pixels before the one JPEG encode; deskewing resamples the picture (slightly softer) and took about 2 seconds for an A4 page at 300 dpi in a test, and trimming leaves a page alone if it would cut away nearly everything.
- `dump` prints every option of a device, for comparing what different scanners expose (`dump test` is SANE's virtual test device).

Scanners disagree about option names and values, so look at `dump` output for yours. Only JPEG output exists so far.

## Web UI

`src/SaneTui.Web` serves one page that does what the terminal UI does, from a browser, for a scanner attached to a server: preview a flatbed and drag out the area to scan, scan a flatbed or a whole feeder, watch the pages arrive, and download them as a PDF, optionally with a title and date suggested by a language model. It runs from a container image, protected by a password. See the [web UI documentation](src/SaneTui.Web/README.md) for how to run and configure it.

## Releases

A tag like `v1.2.3` publishes a release: the `sane-tui` program for Linux (x64 and arm64, a single file with .NET inside; it only needs SANE installed) to download from the [releases page](../../releases), and the web image as `ghcr.io/godefroi/sane-tui-web`.

## Tests

The tests need libsane, so on a machine without it (or without Linux) run them in a container:

```sh
podman build -t sanetui-tests -f tests/Dockerfile .
podman run --rm sanetui-tests
```

On Linux with libsane installed, `dotnet test` works directly.

## Notes on SANE

- Every SANE call runs on one dedicated thread. Backends aren't documented as thread-safe, and `sane_init`/`sane_exit` are process-global, so all `SaneLibrary` instances in a process share one session.
- Backends differ in option names, units (mm vs pixels), and number types (int vs fixed) for the same logical option. The library reports what the backend says and doesn't paper over it.

## License

Apache 2.0, see [LICENSE](LICENSE).
