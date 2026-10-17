# syntax=docker/dockerfile:1
# MKITS-SupportSuite server image.
# Build from the repository root:  docker build -f deploy/docker/Dockerfile -t mkits-server .
# The container listens on plain HTTP 8080; TLS is terminated by the reverse proxy (nginx).

ARG DOTNET_VERSION=10.0

# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Restore first (better layer caching): only build configuration and project files.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Common/MKITS.SupportSuite.Common/MKITS.SupportSuite.Common.csproj src/Common/MKITS.SupportSuite.Common/
COPY src/Server/MKITS.SupportSuite.Server/MKITS.SupportSuite.Server.csproj src/Server/MKITS.SupportSuite.Server/
RUN dotnet restore src/Server/MKITS.SupportSuite.Server/MKITS.SupportSuite.Server.csproj

COPY src/Common/ src/Common/
COPY src/Server/ src/Server/
RUN dotnet publish src/Server/MKITS.SupportSuite.Server/MKITS.SupportSuite.Server.csproj \
    -c Release --no-restore -o /app/publish /p:UseAppHost=false

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
LABEL org.opencontainers.image.title="MKITS-SupportSuite Server" \
      org.opencontainers.image.source="https://github.com/MK-ITS/MKITS-SupportSuite" \
      org.opencontainers.image.licenses="AGPL-3.0-only"

WORKDIR /app
COPY --from=build /app/publish .

# Persistent data (SQLite database, recordings, keys) lives in /data – mount a volume there.
ENV ASPNETCORE_URLS=http://+:8080 \
    MKITS_DATA_DIR=/data
RUN mkdir -p /data && chown "$APP_UID" /data
VOLUME ["/data"]

EXPOSE 8080
# Non-root user provided by the official .NET images.
USER $APP_UID
ENTRYPOINT ["dotnet", "MKITS.SupportSuite.Server.dll"]
