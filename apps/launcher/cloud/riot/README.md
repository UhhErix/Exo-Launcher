# Exo Riot hours service (cloud/riot)

Automatic Riot Games playtime aggregation for Exo Launcher (`GET https://riot.getexo.app/v1/hours?riotId=Name%23TAG[&region=na]`).

## Why this exists

`tracker.gg` blocks automated desktop clients with Cloudflare managed challenges (HTTP 403), and shipping developer API keys inside `ExoLauncher.exe` violates ADR-0008. This Worker keeps provider keys strictly server-side as Cloudflare Worker secrets and returns cached, match-derived hours per Riot game and mode.

## Providers (configured via Worker secrets)

- **League of Legends & Teamfight Tactics (`RIOT_API_KEY`):** Official Riot Developer API (`account-v1`, `lol/match/v5`, `tft/match/v1`). Individual match durations are cached in KV for 30 days so repeat refreshes and shared matches do not re-fetch match details.
- **VALORANT (`HENRIK_API_KEY`):** HenrikDev API (`valorant/v2/account`, `valorant/v1/by-puuid/stored-matches`) summing match `game_length` with per-mode breakdown matching `ExoLauncher/Services/RiotModes.cs`.

Either secret can be configured independently; configured providers run in parallel while unconfigured ones report `"unconfigured"` in `coverage`.

## Run the tests

```
npm install
npm test
```

## Privacy

- `[observability]` is disabled (no Worker request logs).
- No CORS headers (meant for Exo Launcher only).
- Aggregated player results are cached in KV under a SHA-256 hash of the normalized Riot ID (`hours:v1:<hash>`, TTL 6 hours) so raw Riot IDs are never used as KV keys.
