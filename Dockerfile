# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Banori/Banori.csproj Banori/
COPY Banori.Core/Banori.Core.csproj Banori.Core/
RUN dotnet restore Banori/Banori.csproj

COPY . .
RUN dotnet publish Banori/Banori.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:7340 \
    ASPNETCORE_HTTP_PORTS=7340
EXPOSE 7340

RUN mkdir -p /app/data \
    && chown -R $APP_UID:$APP_UID /app
COPY --from=build --chown=$APP_UID:$APP_UID /out .
USER $APP_UID

ENTRYPOINT ["dotnet", "Banori.dll"]
