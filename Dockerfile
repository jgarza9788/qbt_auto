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

# curl is needed for the container HEALTHCHECK below; gosu lets the entrypoint
# drop from root to the non-root "app" user after fixing volume ownership.
# Neither is in the aspnet runtime image by default.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl gosu \
    && rm -rf /var/lib/apt/lists/*

# /data holds the SQLite DB and the data-protection key ring; /log holds the rolling
# log files. chown them now so the non-root "app" user (built into the aspnet image
# since .NET 8) can write to them. A bind mount will re-set this to the host dir's
# owner, which is why the entrypoint re-applies the chown at runtime.
RUN mkdir -p /data /log && chown app:app /data /log

COPY --from=build /app .
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    QBITFLOW_DATA_DIR=/data \
    QBITFLOW_LOG_DIR=/log \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080
VOLUME ["/data", "/log"]

# Deliberately NOT `USER app` here: the entrypoint starts as root so it can chown
# freshly bind-mounted /data and /log, then exec's the app as "app" via gosu.

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/healthz || exit 1

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["dotnet", "Qbitflow.Web.dll"]
