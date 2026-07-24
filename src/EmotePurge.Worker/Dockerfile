FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

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
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "EmotePurge.Worker.dll"]
