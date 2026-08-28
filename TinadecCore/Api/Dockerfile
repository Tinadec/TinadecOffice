# TinadecCore API container image (A4 slice).
# Build from the repository root so the nested project references resolve:
#   docker build -f TinadecCore/Api/Dockerfile -t tinadec-core-api:dev .
# Registry push is intentionally not part of the workflow yet (no container
# repository has been chosen); CI builds the image to keep the slice green.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY TinadecCore/ ./TinadecCore/
RUN dotnet restore TinadecCore/Api/TinadecCore.Api.csproj
RUN dotnet publish TinadecCore/Api/TinadecCore.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# SQLite database and Core-owned file stores (sessions/tasks/events/artifacts/
# vectors) live under one mountable root.
ENV ASPNETCORE_URLS=http://0.0.0.0:48731 \
    TinadecPersistence__Sqlite__DatabasePath=/data/tinadec.db \
    TinadecPersistence__DataRoot=/data
VOLUME /data
EXPOSE 48731
ENTRYPOINT ["dotnet", "TinadecCore.Api.dll"]
