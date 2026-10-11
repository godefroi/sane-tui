# Integration tests need libsane; build context is the repo root:
#   podman build -t sanetui-tests -f tests/Dockerfile . && podman run --rm sanetui-tests
FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update && apt-get install -y --no-install-recommends libsane1 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY sane-tui.slnx ./
COPY src/SaneTui.Sane/SaneTui.Sane.csproj src/SaneTui.Sane/
COPY src/SaneTui/SaneTui.csproj src/SaneTui/
COPY tests/SaneTui.Sane.Tests/SaneTui.Sane.Tests.csproj tests/SaneTui.Sane.Tests/
RUN dotnet restore
COPY . .
RUN dotnet build --no-restore
CMD ["dotnet", "test", "--no-build"]
