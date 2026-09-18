# C# migration roadmap

## Goal

Replace the PowerShell production runtime with a small, Windows-only C#/.NET executable while preserving the already researched IS74 protocol and the behavior of the current agent.

The migration is **not** a protocol redesign. The PowerShell implementation remains the reference implementation until the C# runtime reaches behavioral parity and passes field tests on Windows 10 and Windows 11.

Primary goals:

- make the authorization state machine explicit, typed, testable, and deterministic;
- remove PowerShell 5.1/7 compatibility work, StrictMode workarounds, BOM requirements, and PowerShell-specific async/error glue;
- run a true background agent without Console Host / Windows Terminal artifacts;
- classify DNS, timeout, HTTP, authentication, and ambiguous-side-effect failures explicitly instead of surfacing wrapper exceptions;
- keep distribution simple: a small portable Windows utility, no GUI framework and no installer requirement;
- preserve the existing protocol timings and safety limits unless a separate field experiment justifies a change.

## Technology baseline

- Language: C#.
- Runtime: .NET 10 LTS.
- Target: `net10.0-windows10.0.19041.0` for the first implementation.
- Application subsystem: `WinExe` so background `agent` mode never creates a console window.
- Manual mode: the same executable attaches to an existing parent console or allocates a console when launched interactively.
- Autostart target: per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, not Task Scheduler, unless later requirements need scheduler-only features.
- Storage remains under `%LOCALAPPDATA%\IS74Wifi`.
- Secrets remain protected for the current Windows user with DPAPI semantics.
- No heavyweight GUI framework, database, dependency-injection container, or Windows Service in the initial migration.
- Win32 interop policy: prefer source-generated `LibraryImport` on .NET 10. `AllowUnsafeBlocks=true` is enabled for generated stubs; use legacy `DllImport` only when a specific API cannot be expressed cleanly with `LibraryImport`.

.NET 10 is chosen because the migration starts in 2026 and .NET 8 is near end of support. Packaging policy (framework-dependent vs self-contained vs NativeAOT) is intentionally deferred until the runtime is functionally complete; size and startup measurements will decide it.

## Protocol invariants to preserve

These are requirements, not suggestions for the rewrite:

1. API registration can be completed entirely on Windows and produces a long-lived Bearer.
2. `check-confirm` uses an empty `authId` for the normal SMS flow and consumes the returned `authId` for `get-token`.
3. Captive authorization only runs when the currently connected SSID begins with `Campus Wi-Fi` (case-insensitive).
4. Before `stepOne`, read the current top push message ID as `baselineId`.
5. Start one `stepOne` at `T=0` and poll `/pushmessages` at absolute offsets:
   `100, 150, 200, 250, 350, 500, 700, 1000, 1400, 2000, 3000, 4500, 6500, 10000 ms`.
6. Poll requests are independent; a slow earlier request must not delay a later scheduled request.
7. A valid Wi-Fi code must have `id > baselineId`, subject `Ваш код авторизации`, and the exact four-digit message signature already documented in `docs/protocol.md`.
8. `pageSize=1` is the normal path. One asynchronous `pageSize=5` fallback is allowed when a fresh non-code top message could have hidden the Wi-Fi code.
9. A fresh matching code is sufficient to send `stepTwo` immediately. Do not wait for the browser-facing `stepOne` 302.
10. Do not perform browser `GET /stepTwo` or `GET /stepThree`.
11. Already-authorized redirects include both observed landing routes documented in `docs/protocol.md`; unknown redirects fail closed.
12. Automatic authorization may send at most four `stepOne` requests in one automatic cycle.
13. If a `stepOne` response is lost/ambiguous, continue the mailbox polling window. If no fresh code appears, that attempt may be retried within the four-attempt policy; do not immediately duplicate `stepOne`.
14. HTTP 401 from the API is terminal (`bearer-invalid`) and requires re-registration rather than retrying forever.
15. If the `stepTwo` response is lost, never resend the code blindly. Confirm the possible side effect with Internet probes at the existing delayed recovery offsets before declaring the result ambiguous.
16. Successful authorization stores the predicted expiry based on the accepted `stepTwo` server date (with local fallback) plus the observed 24-hour window. Treat it as a scheduling prediction, not a formally guaranteed server SLA.
17. Preserve the 10-second expiry guard behavior and the rule that, at/after predicted expiry, the timer is authoritative once the `Campus Wi-Fi` gate is satisfied.
18. Bearer, full phone number, SMS code, and Wi-Fi confirmation code must not be written to diagnostic logs.

## Error model

C# should expose domain results instead of PowerShell/.NET wrapper exceptions. Initial taxonomy:

- `DnsUnavailable`
- `ApiTimeout`
- `PortalTimeout`
- `BearerInvalid`
- `UnexpectedApiResponse`
- `UnexpectedPortalRedirect`
- `AlreadyAuthorized`
- `CodeNotReceived`
- `StepOneAmbiguous`
- `StepTwoAmbiguous`
- `InternetNotConfirmed`
- `WrongWifi`

Transient DNS/transport/API failures before a captive side effect use bounded retry/backoff. Terminal authentication failures stop and request user action. Ambiguous POST side effects are resolved by observation before any repeat.

## Migration phases

### Phase 0 — freeze the behavior specification

Status: **in progress / mostly captured**.

- Keep `docs/protocol.md` as the protocol source of truth.
- Keep the PowerShell critical-path contract as a reference until equivalent C# tests exist.
- Record migration decisions in this file.
- Do not silently change timings, retry limits, redirect classification, or safety rules during language migration.

Exit gate: the roadmap and C# build skeleton are present in `main`.

### Phase 1 — C# skeleton and CI

Status: **complete**.

- Add `IS74Wifi.Core` for protocol/state-machine logic.
- Add `IS74Wifi.App` as the one Windows executable.
- Use `WinExe`; interactive mode explicitly acquires a console, `agent` mode stays consoleless.
- Add Windows CI with .NET 10 restore/build and a smoke invocation.
- Keep the PowerShell runtime untouched and releasable while migration is incomplete.

Exit gate: C# solution builds on `windows-latest` and the executable can run `version`/`help` without PowerShell.

### Phase 2 — state, secrets, logging, and platform primitives

Status: **implementation complete; Windows CI/field validation pending**. Typed settings/runtime stores, stable device ID, PowerShell-compatible CurrentUser DPAPI secrets, redacted rotating diagnostics, native WLAN enumeration, exact Microsoft Connect Test parsing, named mutexes, and typed asynchronous HTTP transport are implemented. DNS cached-IP resilience remains intentionally deferred to Phase 7.

- Typed configuration/state model.
- Stable device ID.
- DPAPI-compatible per-user secret storage.
- Rotating/redacted diagnostic logger with the same privacy guarantees as the PowerShell client.
- WLAN SSID query via Windows WLAN API.
- Internet connectivity probe with exact `Microsoft Connect Test` validation.
- Single-instance mutexes for agent and authorization transaction.

Exit gate: unit/contract tests cover state persistence, redaction, SSID gating, connectivity probe parsing, and mutex behavior.

### Phase 3 — IS74 API client

- Registration endpoints: `get-confirm`, `check-confirm`, `get-token`.
- Device metadata registration.
- Typed push-message parsing for both observed root-array and object-shaped responses.
- `baselineId` logic and fail-closed parsing.
- Explicit 401 handling.
- Explicit timeout/DNS result mapping.

Exit gate: mocked HTTP contracts reproduce all known registration/push response shapes and failure classes.

### Phase 4 — captive portal client

- `stepOne` and `stepTwo` form encoding.
- No auto-redirect while classifying portal responses.
- Narrow already-authorized redirect allow-list.
- Direct `stepTwo`, no browser page GETs.
- Preserve certificate/name validation for HTTPS endpoints.

Exit gate: deterministic tests cover unauthenticated redirect, both already-authorized redirects, unexpected redirects, success `stepThree`, and lost-response ambiguity.

### Phase 5 — critical polling engine and authorization state machine

- Monotonic stopwatch and absolute-offset scheduling.
- Independent concurrent GETs.
- Immediate cancellation of future launches after a valid code is found.
- Asynchronous one-shot `pageSize=5` fallback that cannot block the primary schedule.
- Four-attempt `stepOne` budget and existing backoff.
- Lost `stepTwo` Internet-confirmation window.
- Detailed in-memory timing telemetry flushed after the critical phase.

Exit gate: C# tests reproduce the existing PowerShell critical-path scenarios, including a deliberately stalled `stepOne` response that must not delay code → `stepTwo`.

### Phase 6 — agent and autostart

- Long-running consoleless `agent` mode.
- Dynamic sleeping outside the expiry guard.
- 24-hour predicted expiry scheduling and guard behavior.
- Per-user `HKCU ...\Run` install/disable/uninstall operations.
- `uninstall` removes the Run entry and local app data; portable program files remain user-managed.
- Manual `connect`, `status`, `logs`, `reset`, and `purge` commands.

Exit gate: Windows 10 and Windows 11 field tests confirm no terminal window remains open and autostart can be enabled/disabled/removed cleanly.

### Phase 7 — DNS resilience

This is intentionally part of the C# rewrite rather than another PowerShell workaround.

- Cache resolved addresses for `api.is74.ru` and `w.is74.ru` while normal Internet is available.
- Keep the original hostname in the request URI/HTTP Host semantics.
- For HTTPS API connections, preserve TLS SNI and certificate hostname validation while using a cached physical IP.
- Fall back to ordinary DNS and refresh the cache when a stored address stops working.
- Never turn DNS failure into a raw user-facing exception string.

Exit gate: integration tests prove cached-IP connection behavior and fallback semantics; a field test reproduces captive DNS failure without breaking push polling.

### Phase 8 — packaging and migration release

Compare three packaging modes on real Windows 10/11 machines:

1. framework-dependent;
2. self-contained single-file;
3. NativeAOT single-file, if all required APIs and serializers remain compatible.

Choose based on binary size, startup time, memory use, antivirus behavior, and zero-dependency usability. Do not choose NativeAOT merely for size if it complicates the code or protocol tests.

Release requirements:

- runtime archive contains the new executable and only intentionally shipped supporting files;
- SHA-256 published;
- clean-machine Win10 and Win11 smoke test;
- registration test;
- already-authorized test;
- real expiry/reauthorization field test;
- DNS failure and API timeout produce controlled retry/status, not raw exceptions;
- autostart is invisible and removable through normal app commands / Windows startup controls.

### Phase 9 — retire PowerShell production runtime

Only after the C# release passes field parity:

- mark PowerShell production files as legacy/reference;
- keep experiments/protocol evidence where useful;
- stop releasing `IS74Wifi.ps1`, `agent.ps1`, and `src/IS74Wifi.psm1` as the primary runtime;
- keep protocol history and regression fixtures.

## Definition of done

The migration is complete when a clean Windows 10 or Windows 11 machine can:

1. download and run the C# build without PowerShell-specific setup;
2. register once by phone/SMS;
3. enable normal per-user autostart;
4. run indefinitely without a visible console/Terminal window;
5. authorize only on `Campus Wi-Fi*`;
6. survive transient DNS/API failures without raw exception UX;
7. perform the proven fast captive flow with the same safety limits;
8. cleanly disable/uninstall autostart and local state;
9. produce redacted diagnostics sufficient to debug field failures.

Until these gates pass, the PowerShell implementation remains the fallback/reference runtime and must not be deleted.
