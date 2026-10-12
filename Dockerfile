# ---- 1. Angular UI -> src/AskDocs.Api/wwwroot
FROM node:22-alpine AS web
WORKDIR /src/src/web
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci
COPY src/web/ ./
RUN npx ng build --configuration production

# ---- 2. .NET API (serves the UI from wwwroot)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api
WORKDIR /src
COPY Directory.Build.props ./
COPY src/AskDocs.Api/AskDocs.Api.csproj src/AskDocs.Api/
RUN dotnet restore src/AskDocs.Api/AskDocs.Api.csproj
COPY src/AskDocs.Api/ src/AskDocs.Api/
COPY --from=web /src/src/AskDocs.Api/wwwroot src/AskDocs.Api/wwwroot
RUN dotnet publish src/AskDocs.Api/AskDocs.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    Storage__DataDirectory=/data
COPY --from=api /app .
RUN mkdir -p /data && chown $APP_UID /data
VOLUME /data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "AskDocs.Api.dll"]
