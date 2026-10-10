# Runs the manager for servers hosted elsewhere (RCON mode).
# Managing a RustDedicated process inside this container isn't supported.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/RustSandbox -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ConnectionStrings__Default="Data Source=/data/rustsandbox.db" \
    Urls="http://0.0.0.0:5080"
VOLUME /data
EXPOSE 5080
ENTRYPOINT ["dotnet", "RustSandbox.dll"]
