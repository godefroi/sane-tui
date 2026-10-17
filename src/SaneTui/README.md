# sane-tui, the terminal program

Scan from a terminal on Linux: an interactive interface for choosing a scanner and its settings, previewing a flatbed and picking the area to scan, and scanning a page or a whole document feeder to JPEG files. It also has plain commands for scripts and for working out why a scanner doesn't work.

It's built on the [`SaneTui.Sane`](../SaneTui.Sane/README.md) library, with Terminal.Gui for the interface. For scanning from a browser instead, see the [web UI](../SaneTui.Web/README.md).

## Getting it

Download `sane-tui-<version>-linux-x64.tar.gz` or `-linux-arm64.tar.gz` from the [releases page](https://github.com/godefroi/sane-tui/releases), unpack it and run `./sane-tui`. It's a single file with .NET inside, so the only thing it needs is SANE: the native library (`libsane1` on Debian and Ubuntu, `sane-backends` on Fedora) and the driver for your scanner. On its first run it unpacks a few native files into a cache folder (`~/.net`).

From a clone of the repository (needs the .NET 10 SDK): `dotnet run --project src/SaneTui`.

## Commands

```sh
sane-tui                          # the interactive interface: pick a scanner, settings and a page size, preview a flatbed, then Scan
sane-tui diag                     # does libsane load? which scanners does SANE find?
sane-tui scan -o page.jpg         # scan with the only scanner, at 300 dpi, full area
sane-tui dump                     # print every option of every device SANE can see
```

**`diag`** is the place to start when nothing works. It reports which libsane was loaded (or why it couldn't be), lists the scanners SANE found, and points out USB devices that look like scanners but weren't claimed by any driver, with a check of whether you may open them.

**`scan`** writes JPEG files (the web service is the one that makes PDFs). Pick a scanner by name (from `diag`) if there's more than one.
- Common settings have flags: `--source`, `--mode`, `--resolution`, and `--paper a4|a5|a6|letter|legal` or `--width` and `--height` in millimeters. Anything else is `--set name=value`, which can be repeated.
- It says what area and resolution it's scanning, and on a terminal it shows progress. A scanner that can't cover the paper size scans what it can and tells you.
- Values are checked against what the scanner allows, and the error says what it does allow.
- Existing files are never replaced unasked. `--if-exists fail` (the default) refuses before scanning, `overwrite` replaces, and `rename` keeps the old files and saves under new names such as `scan (2).jpg`. (The interactive interface asks instead: Yes, No or Cancel.)
- `--batch` scans a whole document feeder to `page-001.jpg`, `page-002.jpg`, and so on, and `--max-pages` stops it early. Use it for feeders: a feeder feeds sheets by itself, so stopping after one can use up the next. Pages already scanned are kept if a later one fails.
- `--rotate-180` turns each saved page half way round, `--deskew` straightens a crooked page, and `--trim` cuts the background away. They're done on the scanner's own pixels before the one JPEG encode. Deskewing resamples the picture (slightly softer) and took about 2 seconds for an A4 page at 300 dpi in a test. Trimming leaves a page alone if it would cut away nearly everything, as a blank sheet would.

**`dump`** prints every option of a device, for comparing what different scanners expose. `dump test` shows SANE's virtual test device.

Scanners disagree about option names and values, so look at `dump` output for yours.

## The interactive interface

Space opens a dropdown, and Esc quits. The settings (scanner, source, mode, resolution, page size) are filled from what the chosen scanner offers, so a feeder-only scanner isn't offered a flatbed. When the source looks like a feeder, "All pages in the feeder" is offered and ticked, and Preview is hidden.

- **Preview** (flatbeds) scans the whole bed at low resolution and shows it in the pane below the settings. If your terminal reports Kitty or Sixel graphics support, the picture is drawn as real pixels; otherwise it's drawn in coloured cells, which is coarse.
- **Choosing the area.** Drag over the preview. A mouse drag snaps to terminal cells, so adjust it from the keyboard afterwards: the arrow keys slide the area by 1 mm and Shift with an arrow moves its right and bottom edges. A click clears it. The area then shows as "Selection" in the page size dropdown.
- **Pages** appear in the pane as they're scanned. PageUp and PageDown step through the pages of the latest scan and back to the bed preview. The list starts over each time Scan is pressed.
- **Rotate 180°, Deskew, Trim** are checkboxes with the same meaning as the `scan` flags. Rotating turns the preview over too, for a page laid on the glass upside-down.
- **Log** shows everything the status line has said.
- Tab and Shift+Tab move between the controls, and out of the preview pane.
