# Auth startup banner — design

**Date:** 2026-09-03
**Status:** Approved

## Problem

When `AUTH_MODE` is `apikey` or `basic`, a new user hitting the UI gets a bare
`Unauthorized` response from `AuthGate` with no hint about how to authenticate.
Working out the correct `?apikey=<secret>` URL (and its URL-encoding quirks) or the
Basic-auth flow is guesswork. There is nothing in the logs or the README that
walks a first-time user through it.

## Goal

On every boot, log one clear, mode-specific block that tells the user exactly how
to reach the UI given the active auth mode. Add a matching "Access control"
section to the README.

## Non-goals

- No change to `AuthGate`'s enforcement behavior (exempt paths, hashing, fail-open).
- No HTML 401 page — logs + README only.
- No "first run only" gating — the banner logs on every startup.
- The banner never writes the secret value to logs.

## Behavior

### When

Inside the existing startup service scope in `Program.cs` (after
`db.Database.Migrate()` and `ConfigImport.RunFirstBootAsync`, before
`app.UseAuthGate()` / `app.Run()`). One call, `await`ed.

### Mode + secret resolution

Reuse the exact precedence `AuthGate` already applies:

| Value | Precedence |
|---|---|
| mode | `AUTH_MODE` env → `AppSetting.AuthMode` in DB → `none` |
| secret configured? | `AUTH_SECRET` env non-empty → yes (source = "AUTH_SECRET env") ; else `AppSetting.AuthSecretHash` non-empty → yes (source = "Settings page, stored hash") ; else no |

To prevent drift, the resolution is extracted from `AuthGate` into a shared
internal helper that both call. Proposed shape:

```csharp
internal static class AuthConfig
{
    public static async Task<string> ResolveModeAsync(AppSettingStore settings, CancellationToken ct);
    public static async Task<(bool Configured, string Source)> ResolveSecretAsync(AppSettingStore settings, CancellationToken ct);
}
```

`AuthGate.UseAuthGate` is refactored to consume `ResolveModeAsync` and the
existing `expectedHash` lookup stays as-is (it needs the hash value, not just
"configured?"). Only the mode lookup is genuinely shared; the secret helper is
new and banner-only. Keep the helper minimal — no behavior change to the gate.

### Port

The app only knows its internal listen port, parsed from `ASPNETCORE_URLS`
(e.g. `http://+:8080` → `8080`). The published/mapped port and host are unknown
to the process, so URLs use a literal `<host>` placeholder and the parsed port.
If `ASPNETCORE_URLS` is missing or unparseable, fall back to `8080`.

### Messages

Delimiter lines and the `── qbit-flow access ──` header frame every variant.

**`none`** — level `Information`:

```
── qbit-flow access ────────────────────────────────
 AUTH_MODE = none : the UI is OPEN to anyone who can reach this port.
 Lock it down with AUTH_MODE=apikey|basic + AUTH_SECRET in
 docker-compose.yml, or from the Settings page.
────────────────────────────────────────────────────
```

**`apikey`** — level `Information`:

```
── qbit-flow access ────────────────────────────────
 AUTH_MODE = apikey  (secret: <SOURCE>)
 browser : http://<host>:<PORT>/?apikey=<AUTH_SECRET>
           → sets a 30-day qf_key cookie, then browse normally
 scripts : curl -H "X-Api-Key: <AUTH_SECRET>" http://<host>:<PORT>/
 note    : URL-encode the key if it contains + / =
────────────────────────────────────────────────────
```

**`basic`** — level `Information`:

```
── qbit-flow access ────────────────────────────────
 AUTH_MODE = basic  (secret: <SOURCE>)
 browser : the browser prompts — username = anything, password = <AUTH_SECRET>
 scripts : curl -u :<AUTH_SECRET> http://<host>:<PORT>/
────────────────────────────────────────────────────
```

`<SOURCE>` is `AUTH_SECRET env` or `Settings page, stored hash`.
`<PORT>` is the resolved internal port.
`<AUTH_SECRET>` is always the literal placeholder — never the real value.

**`apikey` / `basic` with no secret configured** — level `Warning`, replaces the
body with:

```
── qbit-flow access ────────────────────────────────
 AUTH_MODE = <mode> but no secret is set (AUTH_SECRET env / Settings page).
 AuthGate is FAILING OPEN — the UI is currently unprotected.
 Set AUTH_SECRET in docker-compose.yml or configure it on the Settings page.
────────────────────────────────────────────────────
```

An unrecognized mode string is treated as `none` (matches `AuthGate`, which only
acts on exact `apikey` / `basic`).

## Components

| Unit | Responsibility | Depends on |
|---|---|---|
| `AuthStartupBanner.Build(mode, secretConfigured, secretSource, port)` | Pure function: inputs → the exact multi-line string. No I/O. | nothing |
| `AuthStartupBanner.LogAsync(IServiceProvider, CancellationToken)` | Resolve mode + secret via `AuthConfig`, parse port from `IConfiguration`, pick log level, call `Build`, emit via `ILogger`. | `AuthConfig`, `AppSettingStore`, `IConfiguration`, `ILoggerFactory` |
| `AuthConfig.ResolveModeAsync` / `ResolveSecretAsync` | Shared mode/secret precedence. | `AppSettingStore` |
| `Program.cs` | One `await AuthStartupBanner.LogAsync(scope.ServiceProvider, …)` in the startup scope. | above |

The multi-line block is emitted as a **single** log event with embedded `\n`
(one `ILogger.Log` call), so `docker compose logs` shows it as one contiguous
unit. `LogAsync` picks the level via a tiny co-located helper
`AuthStartupBanner.LevelFor(mode, secretConfigured)` — `Warning` only for the
no-secret fail-open case, `Information` otherwise — so the level decision is unit
-testable alongside `Build`.

## Error handling

- Missing / unparseable `ASPNETCORE_URLS` → port falls back to `8080`, no throw.
- DB unreachable when reading `AppSetting.*` — not expected here (the call runs
  after `db.Database.Migrate()` succeeds in the same scope). Any exception from
  `LogAsync` is allowed to propagate; a broken settings store is already fatal to
  startup and surfaces via the existing `catch` in `Program.cs`.
- The banner has no side effects beyond logging.

## Testing

`tests/QbitFlow.Tests/Web/AuthStartupBannerTests.cs`, exercising `Build` (pure,
no host):

- `none` → contains "AUTH_MODE = none", "OPEN", no `?apikey=`.
- `apikey` + secret from env → contains `?apikey=<AUTH_SECRET>`,
  `X-Api-Key: <AUTH_SECRET>`, `secret: AUTH_SECRET env`, the URL-encode note, and
  the resolved port; never contains a real secret (test passes a sentinel and
  asserts absence).
- `basic` + secret from stored hash → contains "username = anything",
  "password = <AUTH_SECRET>", `curl -u :<AUTH_SECRET>`, `secret: Settings page, stored hash`.
- `apikey` with `secretConfigured: false` → contains "FAILING OPEN" and the
  caller maps this to `Warning` (assert via a helper that returns the level, or a
  small `LogLevel` return from `Build`'s companion — keep the level decision
  co-located with the text so one test covers both).
- unknown mode string → same text as `none`.

If asserting the log *level* is awkward without a host, have `LogAsync` delegate
the level choice to a tiny `AuthStartupBanner.LevelFor(mode, secretConfigured)`
so it is unit-testable alongside `Build`.

## README

Under **Configuration**, expand the single `AUTH_MODE` / `AUTH_SECRET` table row
into a short **Access control** subsection:

- one shared secret, no user accounts;
- `none` — open; how to enable a gate;
- `apikey` — `?apikey=` (one-time, 30-day `qf_key` cookie) or `X-Api-Key` header;
  URL-encode `+ / =`;
- `basic` — browser prompt, secret as password (or username); `curl -u :<secret>`;
- env overrides the Settings page;
- note that the same instructions are printed to the container logs on startup.

Keep a trimmed table row that points at the subsection.

## Files touched

- `src/QbitFlow.Web/Startup/AuthStartupBanner.cs` — new
- `src/QbitFlow.Web/Startup/AuthConfig.cs` — new (extracted shared resolution)
- `src/QbitFlow.Web/Startup/AuthGate.cs` — consume `AuthConfig.ResolveModeAsync`
- `src/QbitFlow.Web/Program.cs` — one call in the startup scope
- `tests/QbitFlow.Tests/Web/AuthStartupBannerTests.cs` — new
- `README.md` — Access control subsection
