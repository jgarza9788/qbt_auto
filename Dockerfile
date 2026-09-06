# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

# Publishing against a concrete RID is what keeps `runtimes/` out of the output.
# A RID-agnostic publish ships the e_sqlite3 native binary for all 21 platforms
# SQLitePCLRaw supports (~29 MB) -- win-x64, osx-arm64, browser-wasm, linux-mips64
# and so on -- when exactly one of them can ever load. Must be a musl RID to match
# the Alpine runtime stage below. Override for arm64 hosts:
#   docker build --build-arg RID=linux-musl-arm64 .
ARG RID=linux-musl-x64
WORKDIR /src

COPY Qbitflow.sln .
COPY src/Qbitflow.Core/Qbitflow.Core.csproj src/Qbitflow.Core/
COPY src/Qbitflow.Sources/Qbitflow.Sources.csproj src/Qbitflow.Sources/
COPY src/Qbitflow.Snapshot/Qbitflow.Snapshot.csproj src/Qbitflow.Snapshot/
COPY src/Qbitflow.Engine/Qbitflow.Engine.csproj src/Qbitflow.Engine/
COPY src/Qbitflow.Infrastructure/Qbitflow.Infrastructure.csproj src/Qbitflow.Infrastructure/
COPY src/Qbitflow.Web/Qbitflow.Web.csproj src/Qbitflow.Web/
COPY src/Qbitflow.Tests/Qbitflow.Tests.csproj src/Qbitflow.Tests/
RUN dotnet restore src/Qbitflow.Web/Qbitflow.Web.csproj -r $RID

COPY src/ src/
# --self-contained false keeps this framework-dependent: the shared runtime still
# comes from the base image below, and the RID is here only to prune native assets.
RUN dotnet publish src/Qbitflow.Web/Qbitflow.Web.csproj \
        -c Release -r $RID --self-contained false \
        -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS runtime
WORKDIR /app

# tzdata: rules resolve arbitrary IANA zone IDs at runtime (CronValidator,
# RuleSchedulerService), and compose passes TZ. Alpine ships none by default.
# su-exec: ~20 KB busybox-native stand-in for gosu (~2 MB); the entrypoint uses it
# to drop from root to the non-root "app" user after fixing volume ownership.
# No curl -- busybox already provides the wget used by the HEALTHCHECK below.
#
# /data holds the SQLite DB and the data-protection key ring; /log holds the rolling
# log files. chown them now so the non-root "app" user (built into the aspnet image
# since .NET 8) can write to them. A bind mount will re-set this to the host dir's
# owner, which is why the entrypoint re-applies the chown at runtime.
RUN apk add --no-cache tzdata su-exec \
    && mkdir -p /data /log \
    && chown app:app /data /log

COPY --from=build /app .
COPY --chmod=0755 docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    QBITFLOW_DATA_DIR=/data \
    QBITFLOW_LOG_DIR=/log \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080
VOLUME ["/data", "/log"]

# Deliberately NOT `USER app` here: the entrypoint starts as root so it can chown
# freshly bind-mounted /data and /log, then exec's the app as "app" via su-exec.

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD wget -qO- http://127.0.0.1:8080/healthz >/dev/null || exit 1

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["dotnet", "Qbitflow.Web.dll"]
