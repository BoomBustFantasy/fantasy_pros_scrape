# FantasyProsScrape

A .NET 10 service that pulls FantasyPros weekly consensus ranks (ECR) for QB/RB/WR/TE and
stores them in Supabase for the Boom weekly-ranks page.

It runs as a web app with scheduled background jobs (Quartz.NET), the same shape as
`yahoo_scrape` and `espn_scrape`. This is the first scraper on .NET 10. The plan lives in
`docs/plans/fantasy-pros-ranks-scraper.md`.

## Status

Feature complete through the seed job. The hourly ranks job (Tue 00:00 through Sun 11:00
America/Chicago) resolves, diffs and writes ranks, history and raw runs; the Tuesday 22:00 seed
job creates the week's `WeeklyRankSets`. Both can be triggered on demand over HTTP.
Deployment is via Portainer, see [docs/deploy/portainer.md](docs/deploy/portainer.md).

## Configuration

`appsettings.json` carries the FantasyPros pages, per-position cutoffs, scoring label and time
zone. Secrets come only from environment variables, which override `appsettings.json`
(see `.env.example`):

| Variable | Purpose |
|---|---|
| `Supabase__Url` | Project URL |
| `Supabase__ServiceRoleKey` | Required; the app refuses to start without it |
| `BetterStack__SourceToken` | Optional log shipping |
| `BetterStack__Endpoint` | Ingest URL for that source; defaults to `https://in.logs.betterstack.com` |

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

## Deploying

`portainer-stack.yml` at the repo root is the Portainer stack: it runs the private
`jackbruzan/fantasy_pros_scrape:latest` image on host port 9081 and reads every secret from the
stack's environment variables. Step-by-step instructions, the variable list, verification and
rollback are in [docs/deploy/portainer.md](docs/deploy/portainer.md).

## Endpoints

| Endpoint | What it does |
|---|---|
| `GET /health` | Detailed health check (JSON) |
| `GET /health/live` | Liveness probe |
| `GET /health/ready` | Readiness: Supabase REST reachable |
| `POST /api/fantasypros/run` | Trigger the ranks job now; optional `?season=&week=&positions=RB,WR` |
| `POST /api/fantasypros/seed/{season}/{week}` | Seed the week's rank set now; no-op if it exists |

## Layout

```
Program.cs                Startup, DI, health checks, Quartz schedules
Configuration/            SupabaseSettings, FantasyProsSettings
Jobs/                     FantasyProsRanksJob (hourly), SeedWeeklyRanksJob (Tuesday night)
Controllers/              On-demand run and seed triggers
Services/                 FantasyProsClient, EcrPageParser, PlayerResolver, RankDiffer, PayloadHasher
Services/Repositories/    Players, ranks + history, runs, weekly rank sets
Models/                   Ecr (page blob) and Supa (table rows)
portainer-stack.yml       Portainer stack definition
docs/deploy/portainer.md  Deployment guide
FantasyProsScrape.Tests   xunit tests
```

## Tests

```bash
dotnet test
```
