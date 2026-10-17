# The scanner web UI. Build from the repository root:
#   podman build -t sane-tui-web -f src/SaneTui.Web/Dockerfile .
# Run it with a password and the scanner's USB bus (see the README):
#   podman run --rm --device /dev/bus/usb --group-add keep-groups -p 8080:8080 -e Web__Password=... -v sane-tui-data:/data sane-tui-web

# the build runs on the machine doing the building whatever the image is for (the published program is the same for every
# architecture), so building for arm64 does not mean compiling under emulation
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0
WORKDIR /src

COPY sane-tui.slnx Directory.Build.props ./
COPY src/SaneTui.Sane/SaneTui.Sane.csproj src/SaneTui.Sane/
COPY src/SaneTui.Core/SaneTui.Core.csproj src/SaneTui.Core/
COPY src/SaneTui.Web/SaneTui.Web.csproj src/SaneTui.Web/
RUN dotnet restore src/SaneTui.Web/SaneTui.Web.csproj

COPY src/ src/
RUN dotnet publish src/SaneTui.Web/SaneTui.Web.csproj --no-restore -c Release -p:Version=${VERSION} -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0

# libsane1 brings the backends (drivers) with it. Tesseract reads the text of the first pages, to suggest a name for the document (add
# tesseract-ocr-<language> for other languages). The scanner is a USB device, which only root can open unless the host sets up
# permissions, so the image does not switch to the unprivileged user the base image provides.
RUN apt-get update \
	&& apt-get install -y --no-install-recommends libsane1 tesseract-ocr tesseract-ocr-eng \
	&& rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /out .

ENV Web__DataFolder=/data
VOLUME /data
EXPOSE 8080

ENTRYPOINT ["dotnet", "SaneTui.Web.dll"]
