# Hearth web server, with Chromium for the bots' browser and Python for the Code ability.
#   docker compose up -d        (see docker-compose.yml)
# or
#   docker build -t hearth . && docker run -d -p 8080:8080 -v hearth-data:/data -e Hearth__Password=change-me hearth

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Hearth.Core/Hearth.Core.csproj src/Hearth.Core/
COPY src/Hearth.UI/Hearth.UI.csproj src/Hearth.UI/
COPY src/Hearth.Computer/Hearth.Computer.csproj src/Hearth.Computer/
COPY src/Hearth.Web/Hearth.Web.csproj src/Hearth.Web/
RUN dotnet restore src/Hearth.Web/Hearth.Web.csproj
COPY src/ src/
RUN dotnet publish src/Hearth.Web/Hearth.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    DEBIAN_FRONTEND=noninteractive
COPY --from=build /app .
# Chromium + its system libraries (via Playwright), Python for run_python, curl for the health check.
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 curl ca-certificates \
 && dotnet Hearth.Web.dll --install-browser \
 && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    HEARTH_DATA_DIR=/data \
    Hearth__AllowLocalProcesses=true
VOLUME /data
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD curl -fs http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "Hearth.Web.dll"]
