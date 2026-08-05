FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

# .NET's SslStream on Linux dynamically loads libgssapi_krb5 for parts of the TLS handshake path —
# missing from the minimal Debian-slim runtime image by default. Without it, any TLS connection
# that hits that code path (observed: the secure WebSocket handshake to Twitch IRC) fails with
# "libgssapi_krb5.so.2: cannot open shared object file", surfaced by TwitchLib only as a generic
# "Fatal network error" — plain HTTPS calls (e.g. to 7tv.io) apparently don't hit the same path.
RUN apt-get update && apt-get install -y --no-install-recommends \
        libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/EmotePurge.Worker/EmotePurge.Worker.csproj", "src/EmotePurge.Worker/"]
COPY ["src/EmotePurge.Infrastructure/EmotePurge.Infrastructure.csproj", "src/EmotePurge.Infrastructure/"]
COPY ["src/EmotePurge.Core/EmotePurge.Core.csproj", "src/EmotePurge.Core/"]
RUN dotnet restore "src/EmotePurge.Worker/EmotePurge.Worker.csproj"
COPY . .
WORKDIR "/src/src/EmotePurge.Worker"
RUN dotnet build "EmotePurge.Worker.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "EmotePurge.Worker.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
RUN groupadd --system appgroup && useradd --system --gid appgroup --no-create-home appuser
COPY --from=publish /app/publish .
RUN chown -R appuser:appgroup /app
USER appuser
# S3-35: WorkerHealthPublisher touches this file after every fully successful publish cycle (20s
# cadence) — the ENV is what switches the touch on, so path and check live in one place. The
# check fails when the newest touch is older than 2 minutes (6 missed cycles): process hung,
# Redis unreachable, or the publisher died.
ENV Worker__HeartbeatFilePath=/tmp/alive
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD test -n "$(find /tmp/alive -mmin -2 2>/dev/null)" || exit 1
ENTRYPOINT ["dotnet", "EmotePurge.Worker.dll"]
