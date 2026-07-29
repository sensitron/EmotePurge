FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM node:lts AS web-build
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ .
RUN npm run build -- --configuration production

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/EmotePurge.Api/EmotePurge.Api.csproj", "src/EmotePurge.Api/"]
COPY ["src/EmotePurge.Infrastructure/EmotePurge.Infrastructure.csproj", "src/EmotePurge.Infrastructure/"]
COPY ["src/EmotePurge.Core/EmotePurge.Core.csproj", "src/EmotePurge.Core/"]
RUN dotnet restore "src/EmotePurge.Api/EmotePurge.Api.csproj"
COPY . .
WORKDIR "/src/src/EmotePurge.Api"
RUN dotnet build "EmotePurge.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "EmotePurge.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
RUN groupadd --system appgroup && useradd --system --gid appgroup --no-create-home appuser
COPY --from=publish /app/publish .
COPY --from=web-build /web/dist/web/browser ./wwwroot
RUN chown -R appuser:appgroup /app
# Mount point for the Data Protection key ring (DataProtection__KeyPath). appuser has --no-create-home,
# so without an explicitly owned directory there is nowhere writable to persist keys to, and every
# restart would silently invalidate all auth cookies.
RUN mkdir -p /keys && chown appuser:appgroup /keys
USER appuser
ENTRYPOINT ["dotnet", "EmotePurge.Api.dll"]
