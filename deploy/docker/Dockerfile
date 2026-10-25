# syntax=docker/dockerfile:1
# MKITS-SupportSuite server image.
# Build from the repository root:  docker build -f deploy/docker/Dockerfile -t mkits-server .
# The container listens on plain HTTP 8080; TLS is terminated by the reverse proxy (nginx).

ARG DOTNET_VERSION=10.0

# ---- browser viewer (TypeScript → wwwroot/viewer/viewer.js) ----
FROM node:25-bookworm-slim AS viewer
WORKDIR /src
COPY proto/ proto/
COPY src/Viewer/web/package.json src/Viewer/web/package-lock.json src/Viewer/web/
RUN cd src/Viewer/web && npm ci --no-audit --no-fund
COPY src/Viewer/web/ src/Viewer/web/
RUN cd src/Viewer/web && npm run build

# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Restore first (better layer caching): only build configuration and project files.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Common/MKITS.SupportSuite.Common/MKITS.SupportSuite.Common.csproj src/Common/MKITS.SupportSuite.Common/
COPY src/Server/MKITS.SupportSuite.Server/MKITS.SupportSuite.Server.csproj src/Server/MKITS.SupportSuite.Server/
RUN dotnet restore src/Server/MKITS.SupportSuite.Server/MKITS.SupportSuite.Server.csproj

COPY proto/ proto/
COPY src/Common/ src/Common/
COPY src/Server/ src/Server/
COPY --from=viewer /src/src/Server/MKITS.SupportSuite.Server/wwwroot/viewer/ src/Server/MKITS.SupportSuite.Server/wwwroot/viewer/
RUN dotnet publish src/Server/MKITS.SupportSuite.Server/MKITS.SupportSuite.Server.csproj \
    -c Release --no-restore -o /app/publish /p:UseAppHost=false

# ---- Windows client / agent (single-file EXE, cross-compiled; EnableWindowsTargeting) ----
# Bundled so that the customer portal and the admin agent download always serve the client of the same version.
# A file in /data/agents/windows-x64/ takes precedence (e.g. a code-signed build, ADR-0009).
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS windows-client
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Common/MKITS.SupportSuite.Common/MKITS.SupportSuite.Common.csproj src/Common/MKITS.SupportSuite.Common/
COPY src/Agent/MKITS.SupportSuite.Agent.Core/MKITS.SupportSuite.Agent.Core.csproj src/Agent/MKITS.SupportSuite.Agent.Core/
COPY src/Agent/MKITS.SupportSuite.Agent.Windows/MKITS.SupportSuite.Agent.Windows.csproj src/Agent/MKITS.SupportSuite.Agent.Windows/
COPY src/Tools/MKITS.SupportSuite.UpdateSigner/MKITS.SupportSuite.UpdateSigner.csproj src/Tools/MKITS.SupportSuite.UpdateSigner/
RUN dotnet restore src/Agent/MKITS.SupportSuite.Agent.Windows/MKITS.SupportSuite.Agent.Windows.csproj -r win-x64 \
 && dotnet restore src/Tools/MKITS.SupportSuite.UpdateSigner/MKITS.SupportSuite.UpdateSigner.csproj

COPY proto/ proto/
COPY deploy/keys/ deploy/keys/
COPY src/Common/ src/Common/
COPY src/Agent/MKITS.SupportSuite.Agent.Core/ src/Agent/MKITS.SupportSuite.Agent.Core/
COPY src/Agent/MKITS.SupportSuite.Agent.Windows/ src/Agent/MKITS.SupportSuite.Agent.Windows/
COPY src/Tools/MKITS.SupportSuite.UpdateSigner/ src/Tools/MKITS.SupportSuite.UpdateSigner/

# Build number orders agent updates (CI: commit timestamp). 0 = local build, never offered as update (ADR-0023).
ARG BUILD_NUMBER=0
RUN dotnet publish src/Agent/MKITS.SupportSuite.Agent.Windows/MKITS.SupportSuite.Agent.Windows.csproj \
    -c Release -r win-x64 --self-contained --no-restore -p:BuildNumber=${BUILD_NUMBER} \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
    -o /out/windows-x64

# Signed update manifest – only if the release key is passed as BuildKit secret "agent_update_key" (CI secret
# AGENT_UPDATE_SIGNING_KEY). The key never ends up in an image layer.
RUN --mount=type=secret,id=agent_update_key \
    if [ -s /run/secrets/agent_update_key ] && [ "${BUILD_NUMBER}" != "0" ]; then \
      dotnet run --project src/Tools/MKITS.SupportSuite.UpdateSigner -c Release --no-restore -- \
        /out/windows-x64/MKITS-Support.exe windows-x64 "${BUILD_NUMBER}" "build ${BUILD_NUMBER}" /run/secrets/agent_update_key; \
    else \
      echo "Agent update not signed (no release key or local build) – agents will not update to this build."; \
    fi

# ---- Linux client (AppImage, x86_64) ----
# Same idea as the Windows client: the portal serves the client of the server's version (ADR-0019).
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS linux-client
# The package is called libturbojpeg on Ubuntu (the SDK image's base) and libturbojpeg0 on Debian. dpkg-deb is part of the base.
RUN apt-get update \
 && (apt-get install -y --no-install-recommends libturbojpeg || apt-get install -y --no-install-recommends libturbojpeg0) \
 && apt-get install -y --no-install-recommends curl ca-certificates file \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Common/MKITS.SupportSuite.Common/MKITS.SupportSuite.Common.csproj src/Common/MKITS.SupportSuite.Common/
COPY src/Agent/MKITS.SupportSuite.Agent.Core/MKITS.SupportSuite.Agent.Core.csproj src/Agent/MKITS.SupportSuite.Agent.Core/
COPY src/Agent/MKITS.SupportSuite.Agent.Linux/MKITS.SupportSuite.Agent.Linux.csproj src/Agent/MKITS.SupportSuite.Agent.Linux/
RUN dotnet restore src/Agent/MKITS.SupportSuite.Agent.Linux/MKITS.SupportSuite.Agent.Linux.csproj -r linux-x64

COPY proto/ proto/
COPY deploy/keys/ deploy/keys/
COPY LICENSE THIRD-PARTY-NOTICES.md ./
COPY deploy/linux/ deploy/linux/
COPY src/Common/ src/Common/
COPY src/Agent/MKITS.SupportSuite.Agent.Core/ src/Agent/MKITS.SupportSuite.Agent.Core/
COPY src/Agent/MKITS.SupportSuite.Agent.Linux/ src/Agent/MKITS.SupportSuite.Agent.Linux/
ARG BUILD_NUMBER=0
# No FUSE during image builds: appimagetool runs extracted.
RUN APPIMAGE_EXTRACT_AND_RUN=1 BUILD_NUMBER=${BUILD_NUMBER} deploy/linux/build-appimage.sh /out/linux
# The installed agent as Debian package (ADR-0025), downloaded by Admins next to the enrollment token.
RUN BUILD_NUMBER=${BUILD_NUMBER} deploy/linux/build-deb.sh /out/linux-deb \
 && cp /out/linux-deb/mkits-agent_*_amd64.deb /out/linux/mkits-agent_amd64.deb

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
LABEL org.opencontainers.image.title="MKITS-SupportSuite Server" \
      org.opencontainers.image.source="https://github.com/MK-ITS/MKITS-SupportSuite" \
      org.opencontainers.image.licenses="AGPL-3.0-only"

WORKDIR /app
COPY --from=build /app/publish .
COPY --from=windows-client /out/windows-x64/MKITS-Support.exe* agents/windows-x64/
COPY --from=linux-client /out/linux/MKITS-Support-x86_64.AppImage agents/linux-x64/MKITS-Support.AppImage
COPY --from=linux-client /out/linux/mkits-agent_amd64.deb agents/linux-x64/mkits-agent_amd64.deb

# Persistent data (SQLite database, recordings, keys) lives in /data – mount a volume there.
# The HTTP port (8080) is set in appsettings.json (Kestrel:Endpoints).
ENV Mkits__DataDirectory=/data \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
RUN mkdir -p /data && chown "$APP_UID" /data
VOLUME ["/data"]

EXPOSE 8080
# Non-root user provided by the official .NET images.
USER $APP_UID
ENTRYPOINT ["dotnet", "MKITS.SupportSuite.Server.dll"]
