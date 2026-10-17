# sane-tui

Scan documents on Linux through [SANE](http://www.sane-project.org/), from a terminal or from a browser, on top of a .NET library that talks to SANE directly. Think Skanlite or SimpleScan, without a desktop.

Status: early. It works with flatbed scanners and document feeders; expect rough edges and changes.

## What's here

| | |
|---|---|
| **[`SaneTui.Sane`](src/SaneTui.Sane/README.md)** | The library: list scanners, read and set their options, scan a page or a whole feeder, and get the pixels back. P/Invoke to `libsane`, no other dependencies. Published as a NuGet package, and meant to be useful on its own. |
| **[`sane-tui`](src/SaneTui/README.md)** | The terminal program: an interactive interface with a preview and area selection, plus `scan`, `diag` and `dump` commands. Scans to JPEG. |
| **[Web UI](src/SaneTui.Web/README.md)** | A web page and service for a scanner attached to a server: preview, area selection, feeder scans, PDF download, optional AI-suggested titles. Runs from a container image, behind a password. |
| [`SaneTui.Core`](src/SaneTui.Core/README.md) | The scanning code the terminal program and the web service share. Internal, not published. |
| [`tests`](tests/README.md) | The tests, and how to run them (the container is the easy way). |

## Getting it

- **The terminal program:** a download from the [releases page](https://github.com/godefroi/sane-tui/releases) (Linux, x64 and arm64).
- **The web service:** the container image `ghcr.io/godefroi/sane-tui-web`.
- **The library:** the NuGet package `SaneTui.Sane`.

Each of those pages says how to run it. All three are made from the same release tag and share its version; [how a release is made](docs/releasing.md) is written down too.

## What they need

Linux, because SANE is, and SANE itself: the native library (`libsane1` on Debian and Ubuntu, `sane-backends` on Fedora) and the driver for your scanner. The code is .NET 10.

## Building

```sh
dotnet build sane-tui.slnx
dotnet run --project src/SaneTui       # the terminal program
```

Warnings are errors in every project. The [tests README](tests/README.md) covers running the tests.

## License

Apache 2.0, see [LICENSE](LICENSE).
