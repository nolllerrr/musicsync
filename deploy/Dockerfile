FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props ./
COPY src/MusicSync.Core/MusicSync.Core.csproj src/MusicSync.Core/
COPY src/MusicSync.Server/MusicSync.Server.csproj src/MusicSync.Server/
RUN dotnet restore src/MusicSync.Server/MusicSync.Server.csproj
COPY src/MusicSync.Core/ src/MusicSync.Core/
COPY src/MusicSync.Server/ src/MusicSync.Server/
RUN dotnet publish src/MusicSync.Server/MusicSync.Server.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
USER $APP_UID
COPY --from=build /out/ ./
EXPOSE 8443
ENTRYPOINT ["dotnet", "MusicSync.Server.dll"]
