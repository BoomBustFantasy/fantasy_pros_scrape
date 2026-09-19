# PRD: FantasyPros Ranks Scraper

**Status:** needs-triage
**Plan:** `docs/plans/fantasy-pros-ranks-scraper.md` (schedules, layout, config, milestones)
**Companion PRD:** `boom/docs/plans/weekly-ranks-prd.md`

## Problem Statement

Every week Jack builds his ranks by hand. He opens FantasyPros, reads the consensus order for QB, RB, WR and TE, copies it somewhere he can work on it, reorders players, and publishes. The betting lines and schedule for the week already flow into the database automatically, but the one input his ranks are actually built from — consensus — does not. It is pulled by hand, on whatever day he gets to it, with no record of what consensus said at the time or how it moved after he looked.

That has three costs. The Wednesday-morning ranks routine starts with clerical work instead of judgment. There is no history: when a player jumps from RB18 to RB9 on Saturday after an injury report, nothing captures that it happened or when. And nothing downstream — the site, a future risers-and-fallers view, the seed for his own ranks — can be built on a data source that lives in a browser tab.

## Solution

A scraper, built the same way the ESPN, FantasyCalc and Yahoo scrapers are built, that watches FantasyPros' PPR consensus rankings all week and keeps the database current.

Every hour from Tuesday through Sunday morning it reads the four rankings pages, matches each ranked player to a row in `Players`, and writes the current consensus rank for everyone inside the depth Jack cares about (32 QB, 70 RB, 70 WR, 30 TE). Whenever a player's consensus position changes, a history row records the move. Whenever the page's payload differs from the last one stored, the raw payload is kept so any field can be recovered later.

Tuesday night it seeds Jack's own ranks for the coming week from the current consensus, so Wednesday morning he opens an admin page in Boom and the list is already there in consensus order, waiting to be reordered and published.

## User Stories

1. As Jack, I want consensus ranks pulled automatically every hour during the week, so that I never open FantasyPros to copy a list by hand again.
2. As Jack, I want ranks for QB, RB, WR and TE only, so that positions I don't rank don't clutter the data.
3. As Jack, I want PPR consensus for RB, WR and TE, so that the ranks match the scoring my audience plays.
4. As Jack, I want QB consensus from the standard QB page, so that quarterbacks are covered even though FantasyPros has no PPR variant for them.
5. As Jack, I want the pull limited to the top 32 QB, 70 RB, 70 WR and 30 TE, so that the tables hold the players who matter and not deep-roster fullbacks.
6. As Jack, I want those cutoffs to be configuration, so that I can widen them without a code change.
7. As Jack, I want the scraper to stop pulling Sunday at 11 AM Chicago time and resume Tuesday, so that it isn't churning during games and before FantasyPros has rolled to the next week.
8. As Jack, I want the season and week stamped on every row to come from what FantasyPros itself says it is serving, so that a week never gets mislabeled by a calendar calculation.
9. As Jack, I want each ranked player tied to a `Players.id`, so that ranks join to the same player rows the rest of the app uses — headshots, teams, schedule, values.
10. As Jack, I want the scraper to learn each player's FantasyPros id the first time it resolves them by name, so that every later pull is an exact-id lookup rather than a name match.
11. As Jack, I want a player who can't be matched to a `Players` row to be logged as a warning and skipped, so that a fullback FantasyPros lists as an RB never blocks the pull or creates a junk row.
12. As Jack, I want name matching to be scoped by team and position and to refuse to guess when two rows match equally, so that a rank is never attached to the wrong player.
13. As Jack, I want name matching to survive suffixes, apostrophes and periods (Jr., III, D'Andre, C.J.), so that the players who resolve by hand today also resolve automatically.
14. As Jack, I want a small set of known nickname differences (Hollywood/Marquise Brown, Bam/Zonovan Knight) handled, so that those players don't need manual fixing every season.
15. As Jack, I want a player who is inside the cutoff once to keep updating even if he later drops just below it, so that his history for the week is complete rather than frozen at the moment he fell out.
16. As Jack, I want a history row written whenever a player's consensus rank changes, so that I can see when and how far he moved.
17. As Jack, I want the first appearance of a player in a week recorded as a history row with no previous rank, so that "entered the top 70 on Thursday" is visible.
18. As Jack, I want movement in the expert spread (average, min, max, std dev) to update the current row without writing history, so that history stays a readable list of real moves.
19. As Jack, I want the current row to carry the spread fields, the FantasyPros position rank label, the start/sit grade and the opponent string, so that the site can show them without a second source.
20. As Jack, I want the raw page payload stored when it differs from the last stored one, so that I can recover any field I didn't think to model, without storing 500 identical copies a week.
21. As Jack, I want the change check to ignore FantasyPros' request-time metadata, so that an identical ranking served a minute later isn't stored twice.
22. As Jack, I want a Tuesday-night job to create my weekly rank set for the coming week from the current consensus, so that Wednesday morning the admin page already has a list to reorder.
23. As Jack, I want the seed to do nothing if a set already exists for that week, so that a re-run or a manual trigger can never wipe out edits I've made.
24. As Jack, I want the seed to record each player's consensus rank at seed time, so that the admin and public pages can show how far I moved someone.
25. As Jack, I want to trigger a pull or a seed on demand over HTTP, so that I can recover from a missed run or seed a week early without redeploying.
26. As Jack, I want the scraper to fail loudly if FantasyPros changes its page and the data blob is missing, so that a layout change pages me instead of silently writing nothing.
27. As Jack, I want a one-line summary logged per position per run (ranked, resolved, new, moved, unresolved), so that a glance at BetterStack tells me the pull is healthy.
28. As Jack, I want the scraper deployed and monitored the same way as the other scrapers — Docker Hub image, Portainer stack, health endpoints, BetterStack logs — so that operating it is nothing new.
29. As Jack, I want this to be the first scraper on .NET 10, so that new work moves the fleet forward instead of adding to the .NET 9 backlog.
30. As Jack, I want no secrets committed in this repo, so that the pattern of live keys in `appsettings.json` stops here.
31. As a Boom visitor, I want the consensus rank shown next to Jack's rank on the site, so that I can see where he disagrees with the crowd — this PRD provides the data; the page is in the Boom PRD.

## Implementation Decisions

**Repo and runtime.** New scraper in `fantasy_pros_scrape`, targeting .NET 10, following the established scraper shape: ASP.NET Core host, Quartz for scheduling, the Supabase PostgREST client for data access, BoomBust.Logging and BoomBust.HealthChecks from the private feed (both still target .NET 9 and are consumed as-is), standard resilience handler on the HTTP client, health endpoints, non-root Dockerfile with the NuGet BuildKit secret, GitHub Actions to Docker Hub, and a service entry in the shared scrapers Portainer stack.

**Data source.** FantasyPros embeds a JSON object (`ecrData`) in a script tag on each rankings page. Verified: the page is served from CloudFront to cookieless requests with a 10-minute cache and no bot gate. The scraper uses a plain HTTP client and extracts the blob; no headless browser. Numeric fields in the blob arrive as strings and are coerced on deserialization. Season, week, scoring and position are taken from the blob's top level, never derived.

**Schedules.** Ranks job: top of every hour Tuesday through Saturday, and midnight through 11 AM Sunday, America/Chicago. Seed job: Tuesday 10 PM America/Chicago. Both jobs disallow concurrent execution and accept season/week/position overrides from the trigger's data map so the controller can run one-offs.

**Modules.** Four deep, pure modules with stable interfaces, and a shallow layer around them:

- *EcrPageParser* — HTML in, typed page (metadata plus player list) out. Throws if the blob is absent or the player list is empty.
- *PlayerResolver* — the full `Players` list, one scraped player, and the mapped team and position ids in; a `Players.id` or null out. Resolution order: FantasyPros id match; team-scoped exact normalized-name match; free-agent-bucket exact match; global exact-and-unique match by position; last-name-exact / first-name-prefix fallback by position. An exact match that is ambiguous returns null and does not fall through to fuzzier steps. Name normalization strips periods, apostrophes, suffixes and extra whitespace. A short synonym map handles known nickname divergences. Pure and deterministic; never touches the database.
- *RankDiffer* — the existing current rows for a season/week/position and the resolved scraped rows in; four buckets out: new (insert plus first-appearance history), changed (ECR moved: update plus history), touched (only spread/grade/opponent moved: update, no history), unchanged. Pure.
- *PayloadHasher* — the players array of the blob in, a stable hash out. Hashes only the players array so request-time metadata never changes the result.
- Shallow: a typed HTTP client for FantasyPros; repositories for players, ranks and history, runs, and weekly rank sets; the two Quartz jobs as orchestration; a controller exposing run and seed triggers.

**Writes.** Players are loaded once per job, not per page. Ranks are written as a single upsert per position on the natural key (season, week, position, scoring, player); history rows as a single bulk insert. When a name match succeeds and the player has no FantasyPros id yet, the id is written with a column-level patch so a concurrent Sleeper or ESPN sync can never be overwritten with stale values. The raw run is inserted only when its hash differs from the latest stored hash for that season/week/position; a unique constraint on the hash backstops the check.

**Resolution scope.** Every scraped player inside the configured cutoff is resolved, plus any player who already has a current-week row (so a player who slips from 70 to 74 keeps updating). Unresolved players inside the cutoff are logged at warning level and skipped; the job still succeeds.

**Seed.** Season and week for the seed come from the most recent stored run unless overridden. If a weekly rank set already exists for that season/week, the job logs and exits. Otherwise it creates the set and, per position, inserts the top-N players by current consensus with rank 1..N and the consensus rank recorded as the seeded rank.

**Schema.** Owned by Boom and applied there before this scraper's first write: a `fantasy_pros_player_id` column on `Players`; `FantasyProsRanks` (current), `FantasyProsRankHistory` (ECR moves), `FantasyProsRankRuns` (raw payloads), `WeeklyRankSets` and `WeeklyRanks` (Jack's ranks). The scraper writes with the service-role key and does not own or migrate any of these.

**Configuration.** Base URL, the four page paths with their positions, the per-position cutoffs, the scoring label and the time zone are configuration. Supabase URL, service-role key and BetterStack token come from environment variables using the same names the other scrapers use in Portainer; the committed settings file carries no secrets and the app refuses to start without a service-role key.

## Testing Decisions

A good test exercises a module through its public interface with realistic inputs and asserts on the result, not on how the module got there. Parser tests read a checked-in fixture; resolver and differ tests build small in-memory player lists; none of them touch the network or the database.

Modules under test:

- *EcrPageParser* — a stripped fixture of a real rankings page parses to the expected count, first player and coerced numeric fields; a page without the blob throws.
- *PlayerResolver* — FantasyPros-id fast path; team-scoped exact match; ambiguous exact match returns null and does not fall through; free-agent fallback; global exact-and-unique; prefix fallback; normalization of suffixes, apostrophes and periods; synonym map.
- *RankDiffer* and *PayloadHasher* — new / changed / touched / unchanged classification; history rows only on ECR change, with null previous rank on first appearance; a player below the cutoff who already has a row still updates; two payloads differing only in request-time metadata hash identically.

Prior art: the xunit + Moq test projects alongside `espn_scrape` and `yahoo_scrape`, in particular their PlayerResolver tests, which these follow in structure and naming.

Not tested: the HTTP client, repositories, Quartz jobs and controller — thin wrappers over libraries, verified by the dry-run milestone against production data rather than unit tests.

## Out of Scope

- Half-PPR and standard scoring variants (the scoring column exists; only PPR is pulled).
- Kickers, defenses, IDP, dynasty or draft rankings.
- Historical backfill — FantasyPros only serves the current week, so history begins the week this ships.
- A shared player-resolution package across scrapers. This repo gets its own resolver, deliberately.
- Upgrading the other scrapers or the BoomBust libraries to .NET 10.
- Anything Jack's ranks are displayed or edited on — that is the Boom PRD.

## Further Notes

- The five current ambiguous names (Frank Gore Jr., Stetson Bennett, Thomas Fidone, Kevin Austin, Andrew Beck) are duplicate `Players` rows — one on a team, one in the free-agent bucket. Team scoping resolves all of them; the duplicates themselves are a Sleeper-sync concern, not this scraper's.
- The 12 players who don't resolve today are all fullbacks or ranked RB83+/WR126+/TE120+. None fall inside the cutoffs. This is expected and acceptable.
- Because FantasyPros caches for 10 minutes at the CDN, pulling more often than hourly would return the same payload most of the time; hourly is the right cadence, not a compromise.
- Dry-run first: milestone S1 runs the full pipeline against production and logs the diff without writing, so the resolver is proven on real data before any row is touched.
