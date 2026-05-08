# syntax=docker/dockerfile:1.7

# ─────────── Build ───────────
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

# Csproj layer (cache friendly)
COPY src/Mimir.Api/Mimir.Api.csproj src/Mimir.Api/
RUN dotnet restore src/Mimir.Api/Mimir.Api.csproj

# Source + publish
COPY src/Mimir.Api/ src/Mimir.Api/
RUN dotnet publish src/Mimir.Api/Mimir.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ─────────── Runtime ───────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS runtime
WORKDIR /app

# wget — healthcheck için
RUN apk add --no-cache wget

# Non-root user + DataProtection-Keys dizini (volume mount altında permission denied'i önler)
RUN addgroup -S mimir && adduser -S mimir -G mimir \
    && mkdir -p /home/mimir/.aspnet/DataProtection-Keys \
    && chown -R mimir:mimir /home/mimir/.aspnet
USER mimir

COPY --from=build --chown=mimir:mimir /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD wget -qO- http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Mimir.Api.dll"]
