FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

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
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "EmotePurge.Api.dll"]
