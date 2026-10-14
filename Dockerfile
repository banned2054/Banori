# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Banori/Banori.csproj Banori/
COPY Banori.Core/Banori.Core.csproj Banori.Core/
RUN dotnet restore Banori/Banori.csproj

COPY . .
RUN dotnet publish Banori/Banori.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:7340 \
    ASPNETCORE_HTTP_PORTS=7340 \
    PUID=1654 \
    PGID=1654
EXPOSE 7340

COPY --from=build --chown=$APP_UID:$APP_UID /out .
COPY entrypoint.sh /app/entrypoint.sh
# /app/config 保持 $APP_UID 属主供命名卷 copy-up 继承；默认以 root 启动由 entrypoint
# 修正数据目录属主后降权到 PUID/PGID，兼容 daemon 自动创建的 root 属主 bind mount 目录。
RUN mkdir -p /app/config \
    && chown -R $APP_UID:$APP_UID /app/config \
    && chmod +x /app/entrypoint.sh

ENTRYPOINT ["/app/entrypoint.sh"]
CMD ["dotnet", "Banori.dll"]
