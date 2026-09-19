# FantasyProsScrape

A .NET 10 service that pulls FantasyPros weekly consensus ranks (ECR) for QB/RB/WR/TE and
stores them in Supabase for the Boom weekly-ranks page.

It runs as a web app with scheduled background jobs (Quartz.NET), the same shape as
`yahoo_scrape` and `espn_scrape`. This is the first scraper on .NET 10. The plan lives in
`docs/plans/fantasy-pros-ranks-scraper.md`.

## Status

Repo skeleton (FEAT-2): host, configuration, health endpoints, typed FantasyPros HTTP client,
Docker image and CI. No jobs are registered yet; the scrape and seed jobs land in later tickets.

## Configuration

`appsettings.json` carries the FantasyPros pages, per-position cutoffs, scoring label and time
zone. Secrets come only from environment variables, which override `appsettings.json`
(see `.env.example`):

| Variable | Purpose |
|---|---|
| `Supabase__Url` | Project URL |
| `Supabase__ServiceRoleKey` | Required; the app refuses to start without it |
| `BetterStack__SourceToken` | Optional log shipping |

## Running It

```bash
dotnet run
```

Starts the web server on http://localhost:5220 in Development. Put the Supabase values in
the environment or in a gitignored `appsettings.Development.json` (same shape as `appsettings.json`).

Building needs the private BoomBustFantasy NuGet feed for `BoomBust.Logging` and
`BoomBust.HealthChecks`. Copy `nuget.config.template` to `nuget.config` and add a GitHub
token with `read:packages`. `nuget.config` is gitignored.

Docker builds take the same token as a BuildKit secret so it never lands in a layer:

```bash
docker build --secret id=nuget_token,src=/path/to/token -t jackbruzan/fantasy_pros_scrape .
docker run --env-file .env -p 9081:8080 jackbruzan/fantasy_pros_scrape
```

## Endpoints

| Endpoint | What it does |
|---|---|
| `GET /health` | Detailed health check (JSON) |
| `GET /health/live` | Liveness probe |
| `GET /health/ready` | Readiness: Supabase REST reachable |

## Layout

```
Program.cs                Startup, DI, health checks, and (later) the job schedule
Configuration/            SupabaseSettings, FantasyProsSettings
Services/                 IFantasyProsSource / FantasyProsClient (typed HttpClient)
FantasyProsScrape.Tests   xunit tests
```

## Tests

```bash
dotnet test
```
