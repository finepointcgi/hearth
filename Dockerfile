# Locus web server, with Chromium for the bots' browser and Python for the Code ability.
#   docker compose up -d        (see docker-compose.yml)
# or
#   docker build -t locus . && docker run -d -p 8080:8080 -v locus-data:/data -e Locus__Password=change-me locus

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Locus.Core/Locus.Core.csproj src/Locus.Core/
COPY src/Locus.UI/Locus.UI.csproj src/Locus.UI/
COPY src/Locus.Computer/Locus.Computer.csproj src/Locus.Computer/
COPY src/Locus.Web/Locus.Web.csproj src/Locus.Web/
RUN dotnet restore src/Locus.Web/Locus.Web.csproj
COPY src/ src/
RUN dotnet publish src/Locus.Web/Locus.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    DEBIAN_FRONTEND=noninteractive
COPY --from=build /app .
# Chromium + its system libraries (via Playwright), Python for run_python, curl for the health check.
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 curl ca-certificates \
 && dotnet Locus.Web.dll --install-browser \
 && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    LOCUS_DATA_DIR=/data \
    Locus__AllowLocalProcesses=true
VOLUME /data
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD curl -fs http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "Locus.Web.dll"]
