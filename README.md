# sane-tui

A terminal UI for scanning documents on Linux (think Skanlite or SimpleScan, but in the terminal), built on a .NET library that talks to [SANE](http://www.sane-project.org/) directly. The library is meant to be useful on its own.

Status: early. The library can enumerate devices, open them, read and set options, and scan pages (single, or a whole document feeder). The TUI is a stub that lists the scanners it finds.

## Layout

| Path | What |
|------|------|
| `src/SaneTui.Sane` | The SANE library (`SaneLibrary`, `SaneDevice`, `SaneOption`). P/Invoke to `libsane.so.1`, no other dependencies. |
| `src/SaneTui` | The `sane-tui` executable: a Terminal.Gui interface (the default command) and a `dump` command. |
| `tests/SaneTui.Sane.Tests` | TUnit tests (Microsoft.Testing.Platform) against SANE's virtual `test` backend. |

Targets .NET 10. Linux only, since SANE is.

## Requirements

`libsane1` (Debian/Ubuntu) or your distro's equivalent, plus the backend for your scanner.

## Usage

```sh
dotnet run --project src/SaneTui                 # interactive UI (a stub for now)
dotnet run --project src/SaneTui -- diag         # does libsane load? which scanners does SANE find?
dotnet run --project src/SaneTui -- scan -o page.jpg          # scan with the only scanner, at 300 dpi, full area
dotnet run --project src/SaneTui -- dump         # print every option of every device SANE can see
```

- `diag` is the place to start when nothing works. It reports which libsane was loaded (or why it couldn't be), lists the scanners SANE found, and points out USB devices that look like scanners but weren't claimed by any driver, with a check of whether you may open them.
- `scan` writes JPEG files. Pick a scanner by name (from `diag`) if there's more than one. Common settings have flags (`--source`, `--mode`, `--resolution`, and `--paper a4|a5|a6|letter|legal` or `--width`/`--height` in millimeters); anything else is `--set name=value`. It says what area and resolution it's scanning, and on a terminal it shows progress. A scanner that can't cover the paper size scans what it can and tells you. Values are checked against what the scanner allows, and the error says what it does allow. `--batch` scans a whole document feeder to `page-001.jpg`, `page-002.jpg`, and so on. Pages already scanned are kept if a later one fails.
- `dump` prints every option of a device, for comparing what different scanners expose (`dump test` is SANE's virtual test device).

Scanners disagree about option names and values, so look at `dump` output for yours. Only JPEG output exists so far.

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
