# Deploying the FantasyPros scraper with Portainer

The scraper ships as the private Docker Hub image `jackbruzan/fantasy_pros_scrape`. Every merge
to `main` rebuilds and pushes `:latest` and `:main`; a `v*.*.*` tag also pushes `:1.2.3` and
`:1.2`. Portainer pulls that image and runs it from `portainer-stack.yml` at the repo root.

## Prerequisites

- The Boom migrations for `Players.fantasy_pros_player_id`, `FantasyProsRanks`,
  `FantasyProsRankHistory`, `FantasyProsRankRuns`, `WeeklyRankSets` and `WeeklyRanks` are
  applied to the `foot` Supabase project (issue #3). The scraper writes on its first hourly run
  and fails loudly if a table is missing.
- Portainer has a registry credential for Docker Hub that can pull `jackbruzan/*`. The other
  scrapers' images are private too, so the credential that pulls them pulls this one.
- Host port `9081` is free. ESPN uses `8080` and FantasyCalc `5146`.

## Environment variables

Set these in the stack's **Environment variables** panel in Portainer. They are substituted
into `portainer-stack.yml` at deploy time; the file itself carries no secrets.

| Portainer variable | Maps to | Required | Value |
|---|---|---|---|
| `SUPABASE_URL` | `Supabase__Url` | yes | The `foot` project URL, `https://<ref>.supabase.co` |
| `SUPABASE_SERVICE_ROLE_KEY` | `Supabase__ServiceRoleKey` | yes | Service-role key. The container exits at startup without it. |
| `BETTERSTACK_TOKEN` | `BetterStack__SourceToken` | no | Source token for a new BetterStack source named `fantasy-pros-scrape`. Empty means console and file logging only. |
| `BETTERSTACK_ENDPOINT` | `BetterStack__Endpoint` | no | Ingest URL for that source. The other scrapers use a region-specific host rather than the default `https://in.logs.betterstack.com`, so copy the value BetterStack shows for the new source. |

The names match the ESPN scraper's compose file, so one set of variables can serve every
scraper if they are ever folded into a single stack.

## Deploy

1. Portainer > **Stacks** > **Add stack**.
2. Name: `fantasy-pros-scraper`.
3. Build method: **Web editor**. Paste the contents of `portainer-stack.yml`.
   (Or **Repository** pointing at this repo, branch `main`, compose path `portainer-stack.yml`.
   That needs a GitHub credential in Portainer because the repo is private, but it makes
   later updates a one-click pull.)
4. Add the environment variables above.
5. **Deploy the stack**.

## Verify

From the Portainer host, or anywhere that can reach it on port 9081:

```bash
curl -s http://localhost:9081/health/ready
```

Expect `"status":"Healthy"` with a `supabase` check inside. `/health/live` answers as soon as
the process is up; `/health/ready` also proves the service-role key reaches PostgREST.

Then trigger one run instead of waiting for the top of the hour:

```bash
curl -s -X POST http://localhost:9081/api/fantasypros/run
```

It returns `202` and the job runs in the background. In the container logs (Portainer >
Containers > `fantasy-pros-scraper` > Logs) or in BetterStack, look for one summary line per
position, for example:

```
RB: 70 ranked, 70 resolved, 70 new, 0 moved, 0 unresolved
```

`unresolved` should be `0` for all four positions. Any non-zero count is logged just above it
as a warning naming the player, and the run still succeeds.

Optional query parameters: `?season=2026&week=3&positions=RB,WR` to scope a one-off run.

To seed the current week's rank set by hand instead of waiting for Tuesday 10 PM:

```bash
curl -s -X POST http://localhost:9081/api/fantasypros/seed/2026/3
```

The seed is a no-op if a set already exists for that week.

## Schedules

Both jobs run in America/Chicago regardless of the host clock; the image also pins `TZ` so log
timestamps match.

| Job | When |
|---|---|
| Hourly ranks pull | Top of every hour, Tuesday 00:00 through Saturday 23:00, and Sunday 00:00 through 11:00 |
| Weekly seed | Tuesday 22:00 |

Nothing fires from Sunday noon through Monday. Confirm this over the first weekend by checking
that BetterStack shows no run summaries in that window.

## Update

Push to `main`, wait for the **Push to Docker Hub** workflow to finish, then in Portainer:
Stack > `fantasy-pros-scraper` > **Pull and redeploy** (or **Update the stack** with
"Re-pull image" checked). Both jobs disallow concurrent execution, and the ranks job is
idempotent within the hour, so a redeploy mid-run at worst delays one pull.

## Roll back

Change the `image:` tag in the stack from `latest` to a specific `:main` digest or a `v*`
tag and redeploy. To cut a version tag:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

## Troubleshooting

- **Container exits immediately, log says `Supabase configuration is missing`.** One of the two
  required variables is empty or misnamed. Names are case-sensitive.
- **`/health/ready` is `Unhealthy` on the `supabase` check.** The URL or key is wrong, or the
  host cannot reach Supabase. `/health/live` will still be `Healthy`.
- **Run summary shows `unresolved` above zero inside the cutoffs.** A player FantasyPros ranks
  is not in `Players` under that team and position. The warning line above the summary names
  them. Usually a Sleeper-sync lag; the next hourly run picks the player up once the row exists.
- **`FantasyProsRankRuns` insert fails on a unique constraint.** Two runs raced on the same
  payload. Harmless; the constraint is the backstop for exactly this.
- **Job throws `ecrData` not found.** FantasyPros changed the page. This is meant to fail loudly
  rather than write zeros. Check the page by hand and open an issue.
