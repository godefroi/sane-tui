# sane-tui web UI

One web page, and the service behind it, for scanning from a browser with a scanner attached to a server. It does what the [terminal UI](../../README.md) does, on top of the same SANE library: choose a scanner, source, mode, resolution and page size, preview a flatbed, drag out the area to scan, scan a flatbed or a whole document feeder, and watch the pages arrive.

- **Choosing the area.** Drag over the bed preview. The arrow keys then slide the area by 1 mm and Shift with an arrow resizes it, and there is a zoom for dragging more precisely. A click clears it.
- **Pages.** Scans add to one document, shown as a strip of thumbnails (PageUp and PageDown step through the bed preview and the pages). A page can be deleted, and the document can be downloaded as a PDF or discarded.
- **The PDF** is written directly, with each JPEG page copied in exactly as the scanner delivered it, so nothing is compressed a second time and the original picture can be pulled back out of the PDF byte for byte.
- **Rotate 180, Deskew, Trim.** Done on the scanner's own pixels before the one JPEG encode. Deskew resamples the picture (slightly softer) and takes a second or two a page. Trim leaves a page alone if it would cut away nearly everything, as a blank sheet would.
- **The scanner's own options.** Its settable on/off options (a driver's feeder skew and crop settings, for instance) are read from the scanner for the source chosen, and opened with "All scanner options..." in a dialog that has each one's help text. Ticking "Show in main panel" for an option also puts it in the settings panel. Which options are shown there, and what you chose for them, is remembered in this browser (local storage), for each model of scanner, and "Reset" goes back to the scanner's own settings. Only the options whose value you changed are sent with a scan.
- **Naming.** See [below](#naming).

The service owns the scanner. There is one operation at a time (a second request is refused while one runs), scans carry on if the browser is closed, and the pages are kept on disk, so a reload, a second tab or a restart picks up where things were.

## Running it

From the container image (a release publishes it for amd64 and arm64):

```sh
podman run --rm --device /dev/bus/usb -p 8080:8080 -e Web__Password=change-me -v sane-tui-data:/data ghcr.io/godefroi/sane-tui-web:latest
```

Then open <http://localhost:8080/>; the user name is `scan`. The image is built from [`Dockerfile`](Dockerfile) in this folder, from the root of the repository:

```sh
podman build -t sane-tui-web -f src/SaneTui.Web/Dockerfile .
```

Without a container (needs `libsane1` and, for naming, `tesseract-ocr`): `Web__Password=change-me dotnet run --project src/SaneTui.Web`.

**The scanner has to be visible inside the container.** `--device /dev/bus/usb` passes the USB bus through. The device nodes are captured when the container starts, so if the scanner is unplugged, switched off or re-enumerates, restart the container. The program runs as root in the image, because opening a USB device needs it unless the host sets up permissions.

## Settings

Set as environment variables (or in any other .NET configuration source; the double underscore is the section separator).

| Setting | Meaning |
|---|---|
| `Web__Password` | Required. The service **refuses to start** without one. |
| `Web__User` | The user name. Default `scan`. |
| `Web__DataFolder` | Where the document being scanned is kept. Default `data`; `/data` in the container, which is a volume. |
| `Web__AllowAnonymous` | `true` turns authentication off. For local development only. |
| `Naming__Endpoint` | The OpenAI-compatible endpoint of an Azure OpenAI or Microsoft Foundry resource. The resource address is enough; `/openai/v1/` is added if it is not there. |
| `Naming__ApiKey` | The key for that endpoint. |
| `Naming__Model` | The model to ask. On Azure and Foundry this is the name given to the deployment. |
| `Naming__Languages` | Tesseract language codes for the text on the pages, joined with `+`. Default `eng`. Install the matching `tesseract-ocr-<language>` package in the image for others. |
| `Naming__Pages` | How many pages, from the first, to read for text. Default 5. |

## Security

The service is protected with HTTP basic authentication. The password crosses the network unencrypted over plain http, so put the service behind TLS (a reverse proxy) unless the network is yours alone. There is one user and no other access control: anyone with the password can scan, and read the scanned pages.

## Naming

With the `Naming__` settings in place, the page can suggest a title and a date for the document. Tesseract reads the text of the first pages, once per page, and a language model answers with a short title and the date the document itself carries. Only that text goes to the model, never the page pictures.

The model is reached through `Microsoft.Extensions.AI`, using the OpenAI library against the `/openai/v1/` endpoint that Azure OpenAI and Microsoft Foundry both serve.

The title and date box names the downloaded PDF (`2024-03-01 Electric bill.pdf`), and works by hand without any of this being set up. **Suggest a name** asks on demand. **After each scan** asks when a scan finishes, and never overwrites something that has been typed. Whatever the title is, it is cleaned so it is safe as a file name.

## The API

The page is a client of a small JSON API, which is also what the tests drive.

| Route | |
|---|---|
| `GET /api/state` | The whole state: status, progress, log, the document's pages, the preview. |
| `GET /api/events` | The same, as server-sent events: sent in full on connect and after every change. |
| `GET /api/devices`, `GET /api/capabilities?device=&source=` | The scanners, and what one offers (for a source). |
| `POST /api/preview`, `GET /api/preview.jpg` | Preview the whole bed, and fetch the picture. |
| `POST /api/scan`, `POST /api/cancel` | Scan (the pages join the document), or stop what is running. |
| `GET /api/pages/{id}[?w=]`, `DELETE /api/pages/{id}` | A page, or a thumbnail of it; delete it. |
| `GET /api/document.pdf[?title=&date=]`, `DELETE /api/document` | The PDF, named from the title and date if given; discard the document. |
| `GET /api/config`, `POST /api/document/name` | Whether naming is set up; suggest a title and date. |

A request for something the scanner is busy with gets `409`; one that makes no sense gets `400` with the reason.
