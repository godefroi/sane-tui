# sane-tui

A terminal UI for scanning documents on Linux (think Skanlite or SimpleScan, but in the terminal), built on a .NET library that talks to [SANE](http://www.sane-project.org/) directly. The library is meant to be useful on its own.

Status: early. The library can enumerate devices, open them, and read every option with its constraints and current value. Setting options and scanning are next. The TUI is a stub that lists the scanners it finds.

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
dotnet run --project src/SaneTui                 # interactive UI
dotnet run --project src/SaneTui -- dump         # print every option of every device SANE can see
dotnet run --project src/SaneTui -- dump test    # a specific device (here, the virtual test backend)
dotnet run --project src/SaneTui -- dump 'net:host:epsonds:libusb:001:004'
```

`dump` is for comparing what different backends expose.

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
