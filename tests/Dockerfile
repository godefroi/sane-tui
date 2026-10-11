# Integration tests need libsane; build context is the repo root:
#   podman build -t sanetui-tests -f tests/Dockerfile . && podman run --rm sanetui-tests
FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update && apt-get install -y --no-install-recommends libsane1 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY sane-tui.slnx ./
COPY src/SaneTui/SaneTui.csproj src/SaneTui/
COPY tests/SaneTui.Tests/SaneTui.Tests.csproj tests/SaneTui.Tests/
COPY src/SaneTui.Dump/SaneTui.Dump.csproj src/SaneTui.Dump/
RUN dotnet restore
COPY . .
RUN dotnet build --no-restore
CMD ["dotnet", "test", "--no-build"]
