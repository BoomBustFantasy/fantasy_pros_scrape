# syntax=docker/dockerfile:1.4
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the layer caches. BoomBust.* packages live on the private GitHub feed,
# so the token comes in as a BuildKit secret and never lands in an image layer.
COPY FantasyProsScrape.csproj .
RUN --mount=type=secret,id=nuget_token \
    dotnet nuget add source "https://nuget.pkg.github.com/BoomBustFantasy/index.json" \
    --name github \
    --username "BoomBustFantasy" \
    --password "$(cat /run/secrets/nuget_token)" \
    --store-password-in-clear-text && \
    dotnet restore FantasyProsScrape.csproj

COPY . .
RUN dotnet publish FantasyProsScrape.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080

# curl is used by the compose / Portainer healthcheck
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
RUN mkdir -p /app/logs

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
# The image ships tzdata; pin the zone so log timestamps match the Quartz schedules.
ENV TZ=America/Chicago

# The aspnet:10.0 image (Debian 13) no longer ships adduser; use the non-root `app` user the
# .NET images have carried since 8.0 instead of creating one.
RUN chown -R app:app /app
USER app

ENTRYPOINT ["dotnet", "FantasyProsScrape.dll"]
