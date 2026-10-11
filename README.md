# sane-tui

A terminal UI for scanning documents on Linux (think Skanlite or SimpleScan, but in the terminal), built on a .NET library that talks to [SANE](http://www.sane-project.org/) directly. The library is meant to be useful on its own.

Status: early. The library can enumerate devices, open them, and read every option with its constraints and current value. Setting options and scanning are next, then the TUI.

## Layout

| Path | What |
|------|------|
| `src/SaneTui` | The SANE library (`SaneLibrary`, `SaneDevice`, `SaneOption`). P/Invoke to `libsane.so.1`, no other dependencies. |
| `tests/SaneTui.Tests` | TUnit tests (Microsoft.Testing.Platform) against SANE's virtual `test` backend. |
| `tools/SaneTui.Dump` | Prints every option of every device, for comparing what different backends expose. |

Targets .NET 10. Linux only, since SANE is.

## Requirements

`libsane1` (Debian/Ubuntu) or your distro's equivalent, plus the backend for your scanner.

## Dumping a scanner's options

```sh
dotnet run --project tools/SaneTui.Dump             # every device SANE can see
dotnet run --project tools/SaneTui.Dump -- test     # a specific device (here, the virtual test backend)
dotnet run --project tools/SaneTui.Dump -- 'net:host:epsonds:libusb:001:004'
```

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
