# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Qbitflow.sln .
COPY src/Qbitflow.Core/Qbitflow.Core.csproj src/Qbitflow.Core/
COPY src/Qbitflow.Sources/Qbitflow.Sources.csproj src/Qbitflow.Sources/
COPY src/Qbitflow.Snapshot/Qbitflow.Snapshot.csproj src/Qbitflow.Snapshot/
COPY src/Qbitflow.Engine/Qbitflow.Engine.csproj src/Qbitflow.Engine/
COPY src/Qbitflow.Infrastructure/Qbitflow.Infrastructure.csproj src/Qbitflow.Infrastructure/
COPY src/Qbitflow.Web/Qbitflow.Web.csproj src/Qbitflow.Web/
COPY src/Qbitflow.Tests/Qbitflow.Tests.csproj src/Qbitflow.Tests/
RUN dotnet restore src/Qbitflow.Web/Qbitflow.Web.csproj

COPY src/ src/
RUN dotnet publish src/Qbitflow.Web/Qbitflow.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# curl is needed for the container HEALTHCHECK below; the aspnet runtime image
# doesn't include it by default.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# /data holds the SQLite DB and the data-protection key ring; chown it now so the
# non-root "app" user (built into the aspnet image since .NET 8) can write to it
# once a volume is mounted there.
RUN mkdir -p /data && chown app:app /data

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    QBITFLOW_DATA_DIR=/data \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080
VOLUME ["/data"]

USER app

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "Qbitflow.Web.dll"]
