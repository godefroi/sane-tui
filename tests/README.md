# Tests

TUnit tests, run with Microsoft.Testing.Platform (`global.json` selects it for `dotnet test`).

| Project | Tests |
|---------|-------|
| `SaneTui.Sane.Tests` | The library, mostly against SANE's virtual `test` backend. |
| `SaneTui.Tests` | The terminal program and the shared code in `SaneTui.Core`: scan commands, option parsing, the preview geometry and its colours, JPEG writing and page corrections, output file naming. |
| `SaneTui.Web.Tests` | The web service, started in-process: authentication, the scanner and its one-at-a-time gate, documents and the PDF, naming, retention, the event stream. |

## What they need

Most of them need the native SANE library, because they scan the `test` backend. The web tests also read text with Tesseract and draw it with a font. All of it is installed in the test container, so the container is the way to run everything, on any machine:

```sh
podman build -t sanetui-tests -f tests/Dockerfile .
podman run --rm sanetui-tests
```

On Linux with `libsane1`, `tesseract-ocr`, `tesseract-ocr-eng` and `fonts-dejavu-core` installed, `dotnet test --solution sane-tui.slnx` works directly, and that is what CI does on a Linux runner.

Elsewhere (Windows, say), the tests in `SaneTui.Tests` and `SaneTui.Web.Tests` skip what needs SANE and run the rest: `dotnet test --project tests/SaneTui.Tests`. The library's own tests don't skip: without libsane they fail, so run those in the container.

Tests that scan share one virtual scanner, so they run one after another and not in parallel.

## What isn't here

The browser behaviour of the web page (dragging an area, the keyboard, the dialogs, what's remembered per scanner) has been checked by driving a real browser against the service, but those checks are not part of the repository.
