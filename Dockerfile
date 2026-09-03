# syntax=docker/dockerfile:1

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# restore (copy only what the QbitFlow solution needs)
COPY QbitFlow.sln ./
COPY .config/ ./.config/
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore QbitFlow.sln

# publish the web host (framework-dependent; runtime image ships the framework)
RUN dotnet publish src/QbitFlow.Web/QbitFlow.Web.csproj \
      -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
RUN apt-get update \
 && apt-get install -y --no-install-recommends bash curl unrar-free p7zip-full ca-certificates \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./

RUN useradd -u 10001 -m app \
 && mkdir -p /data /data/keys /config /exports /scripts \
 && chown -R app:app /app /data /exports /scripts
USER app

ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Db="Data Source=/data/qbitflow.db" \
    SECRETS_ENCRYPTION=none \
    SECRETS_KEY_DIR=/data/keys \
    EXPORTS_DIR=/exports \
    DOTNET_gcServer=0

EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=5 \
  CMD curl -fsS http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "QbitFlow.Web.dll"]
