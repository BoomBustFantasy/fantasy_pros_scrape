# FantasyPros Ranks Scraper — Project Plan

Repo: `fantasy_pros_scrape` (currently an empty scaffold). Companion plan: `boom/docs/plans/weekly-ranks.md` (tables, RLS, admin form, public page).

## Goal

Pull FantasyPros weekly consensus ranks (ECR) for QB/RB/WR/TE hourly during the week, store the current rank per player with a change-only history, and on Tuesday night seed a `WeeklyRanks` set that Jack reorders and publishes Wednesday morning from the Boom admin page.

Decisions already made (do not re-litigate):

| Decision | Value |
|---|---|
| Runtime | .NET 10 (`net10.0`) — first scraper on 10; the rest stay on 9 for now |
| Scoring | PPR only (`qb.php`, `ppr-rb.php`, `ppr-wr.php`, `ppr-te.php`) |
| Cutoffs | QB 32 · RB 70 · WR 70 · TE 30 (config, not code) |
| Cadence | Hourly, Tuesday 00:00 → Sunday 11:00 America/Chicago |
| Identity | `Players.id`. Unresolved top-N player → `LogWarning` + skip. New column `Players.fantasy_pros_player_id` |
| History | Row only when `rank_ecr` changes |
| Raw payload | Stored only when the `players` array hash differs from the last stored run |
| Seed | Quartz job in this repo, Tuesday 22:00 CT, insert-if-absent, never overwrites |
| Player resolution | Local `PlayerResolver` in this repo (yahoo_scrape's shape). No shared package yet |
| Migrations | Live in `boom/supabase/migrations/`, not here. This repo assumes the tables exist |

## Verified facts (Sep 19, 2026)

- Each rankings page embeds `var ecrData = {...};` in a `<script>`. Fields per player: `player_id` (FP int id), `player_name`, `player_team_id`, `player_position_id`, `player_opponent` ("at BUF" / "vs. CAR"), `player_opponent_id`, `pos_rank` ("RB1"), `rank_ecr`, `rank_min`, `rank_max`, `rank_ave`, `rank_std`, `start_sit_grade`, `player_ecr_delta`, `player_bye_week`, `player_game_kickoff_ts`, `player_game_status`, `player_page_url`. Top level: `year`, `week`, `position_id`, `scoring`, `total_experts`, `last_updated` ("9/19"), `last_updated_ts`, `accessed`, `count`, `players[]`.
- A cookieless fetch returns 200 with the blob, served from CloudFront (`x-cache: Hit`, `cache-control: max-age=600`). No bot gate. Plain `HttpClient` works; Playwright is not needed. Nothing more frequent than 10 min is useful.
- `year`/`week` come from `ecrData`. FantasyPros rolls the week after Monday night, so the Tue–Sun window never straddles two weeks. Do not use `NFLCalendar`.
- Against today's `Players` table, every player inside the cutoffs resolves by normalized name + position (all 549 ranked skill players: 532 unique, 5 duplicate-row ambiguities resolved by team, 12 misses — all fullbacks or RB83+/WR126+/TE120+).

## Layout

Same shape as `yahoo_scrape` / `espn_scrape`:

```
FantasyProsScrape.csproj          Microsoft.NET.Sdk.Web, net10.0
Program.cs                         host, DI, Quartz schedules, health endpoints
Configuration/
  SupabaseSettings.cs              Url / ServiceRoleKey (env: Supabase__*)
  FantasyProsSettings.cs           BaseUrl, Pages[], Cutoffs{QB,RB,WR,TE}, TimeZone
Jobs/
  FantasyProsRanksJob.cs           hourly scrape → ranks / history / runs
  SeedWeeklyRanksJob.cs            Tuesday night seed
Services/
  IFantasyProsSource / FantasyProsClient.cs     typed HttpClient, one page fetch
  EcrPageParser.cs                 static: html → EcrPage (regex + System.Text.Json)
  PlayerResolver.cs                static, pure, unit-tested
  RankDiffer.cs                    static: (current rows, scraped page) → inserts/updates/history
  Repositories/
    IPlayerRepository / PlayerRepository.cs        read all skill players; PATCH fantasy_pros_player_id
    IRankRepository / RankRepository.cs            FantasyProsRanks + FantasyProsRankHistory
    IRunRepository / RunRepository.cs              FantasyProsRankRuns (hash check + insert)
    IWeeklyRankRepository / WeeklyRankRepository.cs WeeklyRankSets + WeeklyRanks (seed)
Models/
  Ecr/EcrPage.cs, EcrPlayer.cs      deserialization shapes for ecrData
  Supa/Player.cs, Team.cs, Position.cs, FantasyProsRank.cs, FantasyProsRankHistory.cs,
       FantasyProsRankRun.cs, WeeklyRankSet.cs, WeeklyRank.cs
Controllers/FantasyProsController.cs   POST /api/fantasypros/run, POST /api/fantasypros/seed/{season}/{week}
Utils/TeamAbbreviations.cs         FP ↔ Teams.abbreviation (JAC/JAX, WSH/WAS etc. — verify against Teams table)
FantasyProsScrape.Tests/           xunit + Moq, fixtures/ppr-rb.html
Dockerfile, .github/workflows/docker_publish.yml, nuget.config.template, .env.example
```

Packages: `Quartz` 3.15 (+ DI + Hosting), `Supabase` 1.1.1, `Microsoft.Extensions.Http.Resilience` 10.x, `BoomBust.Logging` 1.0.1, `BoomBust.HealthChecks` 1.0.0 (both target net9 and are consumable from net10 — no republish needed), Serilog sinks as in espn. No HtmlAgilityPack, no Playwright.

## Program.cs

Copy `yahoo_scrape/Program.cs` and change: app name `FantasyProsScrape`, log path `logs/fantasy-pros-scrape-.txt`, the typed client, the two jobs. Keep `/health/live`, `/health/ready`, `/health` with the same JSON writer; keep the `AddSupabaseHealthCheck` wiring.

Schedules (Quartz cron, 6 fields, `.InTimeZone(America/Chicago)`):

```csharp
// FantasyProsRanksJob — top of every hour, Tue–Sat all day, Sun 00:00–11:00
.WithCronSchedule("0 0 * ? * TUE-SAT", x => x.InTimeZone(chicago))
.WithCronSchedule("0 0 0-11 ? * SUN", x => x.InTimeZone(chicago))
// SeedWeeklyRanksJob — Tuesday 22:00
.WithCronSchedule("0 0 22 ? * TUE", x => x.InTimeZone(chicago))
```

Both jobs are `[DisallowConcurrentExecution]`, `.StoreDurably()`, and read optional `season` / `week` / `positions` overrides from `MergedJobDataMap` so the controller can trigger a one-off run or backfill. The aspnet:10.0 image ships tzdata; add `ENV TZ=America/Chicago` anyway so log timestamps match.

## FantasyProsRanksJob

Per configured page (4 requests, ~150 ms apart):

1. `FantasyProsClient.GetPageAsync(path)` → HTML. UA `FantasyProsScraper/1.0 (+boombustfantasy.com)`. Standard resilience handler (3 retries, 30 s timeout) as in espn.
2. `EcrPageParser.Parse(html)` → `EcrPage`. Regex: `var ecrData = (\{.*?\});\s*\n` with `Singleline`. Deserialize with `JsonNumberHandling.AllowReadingFromString` — `rank_min`, `rank_max`, `rank_ave`, `rank_std`, `week`, `year` arrive as strings. Throw (job fails loudly) if the blob is missing or `players` is empty; a layout change must page, not silently write zeros.
3. **Run capture.** `hash = SHA256(JSON of players[] exactly as embedded)`. Hash the `players` array only — the top-level object carries `accessed` and `last_updated_ts`, which change every request. If `RunRepository.LatestHashAsync(season, week, positionId)` ≠ `hash`, insert a `FantasyProsRankRuns` row with the full `ecrData` as jsonb. Otherwise skip.
4. **Resolve.** Load all `Players` where position ∈ {QB,RB,WR,TE} once per job (not per page) with `Teams` abbreviation joined via `team_id`. For each scraped player inside the cutoff (`rank_ecr <= cutoff[position]`) — plus any player who already has a `FantasyProsRanks` row for this season/week/position, so a player who dropped from RB70 to RB74 keeps updating instead of freezing — call `PlayerResolver.Resolve(...)`.
5. **Diff.** `RankDiffer.Diff(existingRows, resolvedPlayers)` yields: new rows (no row yet → insert, history row with `previous_rank_ecr = null`), changed rows (`rank_ecr` differs → update, history row), touched rows (only `rank_ave`/`min`/`max`/`std`/`start_sit_grade`/`player_opponent` moved → update, **no** history row), unchanged (nothing).
6. **Write.** One `Upsert` of new+changed+touched rows on `(season, week, position_id, player_id)`; one bulk `Insert` of history rows. Log a one-line summary per position: `RB: 70 ranked, 70 resolved, 3 new, 9 moved, 0 unresolved`.

Unresolved player inside the cutoff → `LogWarning("Unresolved FantasyPros player {Name} ({FpId}, {Team} {Pos}, ECR {Rank})")` and skip. This is a warning, not an error; the job still succeeds.

## PlayerResolver

Pure static, same contract as `yahoo_scrape/Services/PlayerResolver.cs`:

```csharp
public static long? Resolve(IReadOnlyList<Player> allPlayers, EcrPlayer fp, long? supabaseTeamId, long positionId)
```

1. **FP id fast path** — `Players.fantasy_pros_player_id == fp.player_id` → done.
2. **Team-scoped name match** — candidates where `team_id == supabaseTeamId && position_id == positionId`, compare `NormalizeName(first + " " + last)` to `NormalizeName(fp.player_name)`. Exact match first; if exactly one, done. If >1 exact, return null (ambiguity guard — do not fall through to fuzzy).
3. **Free-agent bucket** (`team_id = 33`) — same match, for players FP still lists on a team Sleeper has since released.
4. **Global exact + unique** by position — catches team-abbreviation mismatches.
5. **Prefix fallback** — exact last name, bidirectional first-name prefix (`Josh`/`Joshua`), still position-scoped, still must be unique.

`NormalizeName`: trim, upper, strip `.` `'` `’`, collapse whitespace, strip trailing ` JR|SR|II|III|IV|V`. Known FP quirks to handle in a small synonym map: `Hollywood Brown` → `Marquise Brown`, `Bam Knight` → `Zonovan Knight`, `Chig Okonkwo` → `Chigoziem Okonkwo` (ktc has the same list — copy it, it's three lines).

When a name match succeeds and `Players.fantasy_pros_player_id` is null, PATCH it column-level (`.Set(p => p.FantasyProsPlayerId, id).Set(p => p.UpdatedAt, now).Update()`) — never a full-row `Update`, so a concurrent Sleeper/ESPN sync can't be clobbered. After the first full week almost every resolve hits step 1.

Position ids come from `Positions` by name at startup; FP's `player_position_id` is already `QB|RB|WR|TE` on these pages. Fullbacks appear on the RB page under `RB` and won't resolve — that's the expected, accepted miss.

## SeedWeeklyRanksJob

Tuesday 22:00 CT (or `POST /api/fantasypros/seed/{season}/{week}`):

1. Determine `season`/`week`: take them from the most recent `FantasyProsRankRuns` row (i.e., whatever FantasyPros is currently serving), unless overridden.
2. If a `WeeklyRankSets` row exists for `(season, week)` → log and exit. **Never** touch an existing set.
3. Insert `WeeklyRankSets (season, week, seeded_at = now, published_at = null)`.
4. For each position, read current `FantasyProsRanks` rows for that season/week ordered by `rank_ecr`, take the first `cutoff[position]`, and insert `WeeklyRanks (set_id, position_id, player_id, rank = 1..N, seeded_rank = rank_ecr)`.
5. Log `Seeded week {Week}: QB 32, RB 70, WR 70, TE 30`.

The admin page in Boom takes it from there (see the boom plan).

## Configuration

`appsettings.json` (committed, no secrets):

```json
{
  "FantasyPros": {
    "BaseUrl": "https://www.fantasypros.com",
    "Pages": [
      { "Position": "QB", "Path": "/nfl/rankings/qb.php" },
      { "Position": "RB", "Path": "/nfl/rankings/ppr-rb.php" },
      { "Position": "WR", "Path": "/nfl/rankings/ppr-wr.php" },
      { "Position": "TE", "Path": "/nfl/rankings/ppr-te.php" }
    ],
    "Cutoffs": { "QB": 32, "RB": 70, "WR": 70, "TE": 30 },
    "Scoring": "PPR",
    "TimeZone": "America/Chicago"
  },
  "Supabase": { "Url": "", "ServiceRoleKey": "" },
  "BetterStack": { "SourceToken": "" }
}
```

Secrets only via env (`Supabase__Url`, `Supabase__ServiceRoleKey`, `BetterStack__SourceToken`) — the same names the other scrapers use in the Portainer stack. Unlike `espn_scrape`, do not commit real keys in `appsettings.json`; ship `.env.example` and fail fast at startup if `ServiceRoleKey` is empty (espn already does the fail-fast part).

## Tests (`FantasyProsScrape.Tests`)

- `EcrPageParserTests` — parse a checked-in `fixtures/ppr-rb.html` (strip it to the `<script>` block, ~80 KB); asserts count, first player, string→number coercion, and that a page without the blob throws.
- `PlayerResolverTests` — FP-id fast path; team-scoped exact; ambiguous exact returns null and does not fall through; free-agent fallback; prefix fallback; suffix/apostrophe normalization; synonym map.
- `RankDifferTests` — new/changed/touched/unchanged classification; history rows only on `rank_ecr` change; dropped-below-cutoff player still updates.
- `PayloadHashTests` — same players array with different `accessed` → same hash.
- `ScheduleTests` — the three cron expressions fire at the expected CT instants around the Sunday 11:00 boundary and DST.

## Build & deploy

- `Dockerfile`: copy `yahoo_scrape/Dockerfile`, bump to `mcr.microsoft.com/dotnet/sdk:10.0` / `aspnet:10.0`, keep the BuildKit `nuget_token` secret and non-root user, add `ENV TZ=America/Chicago`.
- `.github/workflows/docker_publish.yml`: copy from espn, image `jackbruzan/fantasy_pros_scrape`, secret `NUGET_TOKEN`.
- Add a `fantasy-pros-scraper` service to `fantasy_calc_scrape/portainer-stack.yml` next to `fantasy-calc-scraper` / `espn-scraper` (port `9081:8080`, 512M, same env var names). Pull-and-redeploy that stack.

## Milestones

| # | Deliverable | Depends on |
|---|---|---|
| S0 | Boom migrations for `Players.fantasy_pros_player_id`, `FantasyProsRanks`, `FantasyProsRankHistory`, `FantasyProsRankRuns`, `WeeklyRankSets`, `WeeklyRanks` applied to the `foot` project | — (boom plan, B0) |
| S1 | Repo skeleton, parser, resolver, tests. `FantasyProsRanksJob` runs in **dry-run** mode: resolves and logs the diff, writes nothing. Run once against prod to confirm 0 unresolved inside cutoffs | S0 |
| S2 | Writes enabled: ranks, history, runs, `fantasy_pros_player_id` backfill. Deployed to Portainer, hourly schedule live | S1 |
| S3 | `SeedWeeklyRanksJob` + controller endpoint. Manually trigger for the current week to give the admin page data | S2 |
| S4 | First unattended Tuesday-night seed → Wednesday publish from Boom | S3, boom B1 |

Backfill is not a goal: FantasyPros only serves the current week's ECR, so history starts the week this ships.
