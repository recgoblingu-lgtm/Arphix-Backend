FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Revival.Api/Revival.Api.csproj src/Revival.Api/
RUN dotnet restore src/Revival.Api/Revival.Api.csproj
COPY . .
RUN dotnet publish src/Revival.Api/Revival.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /out ./
RUN mkdir -p /data && chown -R ${APP_UID}:${APP_UID} /data
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Database__Provider=Sqlite \
    Database__SqlitePath=/data/revival.db \
    Storage__AssetRoot=/data/assets \
    Storage__BackupRoot=/data/backups
VOLUME ["/data"]
EXPOSE 8080
USER ${APP_UID}
ENTRYPOINT ["dotnet", "Revival.Api.dll"]
