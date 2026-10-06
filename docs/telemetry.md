# Telemetry: local-first authorization traces

## Goal

Telemetry exists to measure the real captive-authorization race without changing it. The most important production question is whether the latency-first path behaves on real machines as designed:

1. `POST /stepOne` starts at `T=0`.
2. `/mobile/pushmessages` GETs start at absolute schedule slots without waiting for earlier GET responses.
3. A fresh Wi-Fi code may be observed while `/stepOne` is still pending.
4. `POST /stepTwo` starts immediately after the fresh code, even when `/stepOne` has not completed.
5. Internet is confirmed independently through the strict `online.susu.ru` probe.

The telemetry implementation therefore records precise local timing first and performs no telemetry HTTP requests inside the authorization critical path. Collection and upload are opt-in: before registration the interactive client asks once for permission to send anonymous application statistics. Declining is remembered and is not asked again on ordinary startup; the preference remains available in Settings.

## Identity and privacy

Each installation gets a permanent random `install_id` (`Guid.NewGuid()` encoded as 32 hexadecimal characters). It is stored separately under `%LOCALAPPDATA%\IS74Wifi\telemetry\install-id.txt` and is not derived from the InterSvyaz device ID, phone number, Windows SID, MAC address, machine name, user/profile ID, or bearer token.

Each authorization has a new random `attempt_id`; each serialized row has a new `event_id`. These IDs allow later SQL/Python grouping and durable deduplication without exposing the InterSvyaz account identity.

The consent state is persisted as `unknown`, `allowed`, or `declined`. `unknown` and `declined` both block authorization telemetry recording, background uploads, automatic speed-test telemetry, and leaderboard publication. Changing consent clears pending upload files so data collected under an earlier policy cannot cross the consent boundary. A local speed test still works without consent. If the user explicitly tries to publish a speed result while consent is not enabled, the UI asks again in that publication context; accepting enables statistics and continues publication, while declining leaves the result local.

Telemetry event contracts deliberately contain no fields for:

- bearer token;
- phone number;
- SMS confirmation code;
- Wi-Fi confirmation code;
- `authId`;
- Cookie / Set-Cookie values;
- `USER_ID` / `PROFILE_ID`;
- InterSvyaz `deviceId`;
- SSID, BSSID/MAC, local IP, gateway, interface GUID or adapter/computer name;
- raw `/mobile/pushmessages` JSON, subject, message body, or full message.

Mailbox telemetry stores only derived facts such as counts, whether a fresh Wi-Fi-code message matched, and small ID deltas from the pre-stepOne baseline. Unexpected portal bodies are not stored; only a bounded classification and SHA-256 may be recorded.

## Local timing model

The timing-critical authorization path only mutates an in-memory `AuthorizationTelemetryTrace`. The stable installation ID is loaded/created when the application runtime is constructed, not during the fast path.

The trace records fractional milliseconds from `Stopwatch`. For the critical captive phase, `T=0` is the launch of `POST /stepOne`.

A mailbox poll row records:

- schedule version;
- poll index;
- planned absolute start;
- actual start;
- completion and duration;
- response completion order;
- `in_flight_at_start`;
- HTTP status/page size/counts;
- whether that poll found the fresh Wi-Fi-code message;
- cache status;
- cancellation reason.

This is sufficient to prove whether a later poll really started while an earlier request was still in flight, rather than merely inferring concurrency from the configured schedule.

The attempt summary additionally records:

- `max_mailbox_in_flight`;
- `overlapping_mailbox_observed`;
- `code_before_step_one_response`;
- `step_two_before_step_one_response`;
- `code_to_step_two_ms`;
- `fast_path_used`;
- `fast_path_success`;
- `internet_confirmed_after_step_two_ms`.

`fast_path_success` requires that the early stepTwo path was actually used, the authorization outcome was successful, and the independent Internet probe confirmed connectivity.

## Path-aware telemetry (schema 5)

Authorization `attempt` rows use schema 5 and may add only coarse path context: `path_kind` (`wifi`, `ethernet`, `other_physical`), whether the local SSID is directly recognized by the existing `Campus Wi-Fi*` policy, whether the path was the preferred physical underlay, whether a VPN route was observed, the pre-attempt path state, and whether an authorization window was known/approximate. No SSID, IP address, gateway, interface GUID or adapter name is serialized. Child timing events (`mailbox_poll`, `internet_probe`, `portal_response`, `error`) remain schema 4 and join to the schema-5 attempt through `attempt_id`.

The agent can also queue a standalone schema-5 `path_observation` when a physical path appears, reappears or changes, when a settling retry was required, or when a scheduled recheck changes/loses connectivity. Stable periodic Internet rechecks are deliberately not emitted. The event records only the reason, initial/final coarse result, path kind, direct-Campus/preferred/VPN booleans, authorization-window flags, settling-retry flag and total local probe duration. A captive result that is about to enter the SMS-critical authorization flow is not synchronously written first; the subsequent authorization trace carries that context after the flow completes.

This preserves the latency-first rule: no telemetry HTTP request occurs in the captive critical path, and the immediate timer-triggered path does not perform extra VPN/interface diagnostics before `stepOne`.

## Registration diagnostics

Schema v4 adds one `registration_event` row for each registration API request that actually runs: `get_confirm`, `check_confirm`, `get_token`, and `device_metadata`. A single random `attempt_id` groups those stages for one registration flow. Each row contains only the stage, request index, success/error/cancelled result, request duration, HTTP status when available, a coarse error class, optional retry delay, and a bounded server header.

Registration telemetry deliberately does not serialize the phone number, SMS code, `authId`, returned token, account identifiers, request/response bodies, device ID, OS/machine metadata, or headers that contain credentials. The API client exposes only transport metadata to the telemetry recorder. The trace is accumulated in memory and queued after the registration flow finishes; if consent is enabled, the app makes a best-effort normal telemetry flush afterward. Upload failure never changes the registration result.

## Local queue

When anonymous statistics are allowed, after the network authorization work has finished the completed trace is serialized into a unique JSONL file under:

```text
%LOCALAPPDATA%\IS74Wifi\telemetry\pending\
```

A queue file contains one completed authorization trace (attempt summary plus detailed poll/probe/portal/error events) or one standalone deferred event such as a significant `path_observation`/speed-test retry. Files are append-only from the analytics point of view and are never modified after publication to the queue.

The queue is bounded (currently about 10 MiB / 1024 trace files). An individually invalid/oversized trace is quarantined rather than blocking later uploads.

A transport batch groups whole trace files, up to 64 events and roughly 60 KiB. The client intentionally stays below the currently deployed Apps Script guards (64 events / 64 KiB) so JSON-envelope overhead cannot push an otherwise valid batch over the receiver limit. `batch_id` is deterministic from the serialized event payload. If an HTTP response is lost, the exact same local data therefore produces the same batch ID on retry. Each row also has its own `event_id` for durable downstream deduplication.

## Upload scheduling

Authorization traces are intentionally not uploaded in real time. Upload scheduling is active only while anonymous statistics consent is `allowed`. After each completed background agent tick, the uploader checks the actual persisted expiry/retry time rather than the agent's normal 15-second sleep interval. A flush is allowed if there is no automatic authorization scheduled (including a clean install), user action is required, the next expiry is safely in the future, or a post-expiry retry is explicitly scheduled far enough away. The safety window is at least one minute and grows with the configured maximum number of batches and HTTP timeout. This keeps telemetry out of the near-expiry authorization window while allowing pending registration traces to leave a newly installed client. The default upload interval is 12 hours.

By default a flush sends one bounded batch. This keeps normal operation close to the intended “accumulate locally, upload rarely” model. The setting can raise that cap for recovery/backlog draining; if data remains after a successful flush, the next attempt is delayed by six hours. HTTP timeout is now 10 seconds by default (clamped to 10–30 seconds at the uploader, including for persisted older 1–3-second settings). This accommodates the Apps Script lock/redirect without performing network work inside authorization. Failures leave files in the local queue and use backoff; authorization never depends on upload success.

The production HTTPS telemetry endpoint is built into the client. `IS74W_TELEMETRY_URL` or the `TelemetryEndpoint` setting can override it for development/testing. Transport failures keep data in the local queue and never block authorization.

`IS74Wifi.exe status` and the interactive diagnostics screen display the pending queue size, last successful telemetry upload, and the next scheduled retry (if any). A failed upload never removes pending files; when troubleshooting an empty Google Sheet, check these fields and `telemetry.upload` entries in `%LOCALAPPDATA%\IS74Wifi\logs\diagnostic.log` before re-registering or attempting another Wi-Fi authorization.

The Google client uses the Windows system proxy configuration. Normal requests write a bounded final response preview, elapsed time, HTTP status/version, and a classified failure (`dns`, `connect`, `tls`, `proxy`, `redirect`, `protocol`, `timeout`, or caller `cancelled`) to `diagnostic.log`. Interactive UI calls use their own 15-second budget by default; the background uploader keeps its independent short timeout.

For an end-to-end Windows trace, run:

```powershell
IS74Wifi.exe backend-diagnose
IS74Wifi.exe backend-diagnose --post
```

The first command performs DNS resolution, manually displays every GET redirect and final body, then repeats the leaderboard read through the production `TelemetryClient`. `--post` additionally sends an invalid empty speed-test payload: it exercises `doPost` and the expected POST-to-GET ContentService redirect without appending a sheet row.

## Ingestion API contract

The Apps Script source is intentionally kept outside this public repository, in the companion private `IntersvyazAutomaticWifi_SERVER` repository (`Code.gs`). The receiver keeps schema 4 as the legacy/default contract and additionally accepts schema 5 only for path-aware authorization `attempt` rows and `path_observation`; registration, speed-test and leaderboard protocols remain schema 4. Every request still requires an explicit route:

```text
POST <web-app endpoint>?route=telemetry         # queued telemetry batch
POST <web-app endpoint>?route=speedtest         # one completed speed_test
POST <web-app endpoint>?route=leaderboard       # one explicit leaderboard_entry
GET  <web-app endpoint>?route=leaderboard&limit=100
POST <web-app endpoint>?route=leaderboardcontrol # status / rename / leave / join
```

The payload `event_type` still selects the row schema (`attempt`, `mailbox_poll`, `internet_probe`, `portal_response`, `registration_event`, `error`, `speed_test`, or `leaderboard_entry`). Authorization traces use a `{batch_id, events[]}` envelope. The receiver guards each request at 64 events / 64 KiB, which is why the local queue emits at most 64 events and targets roughly 60 KiB per transport batch.

The shared `telemetry` route also accepts deferred `speed_test` events, alone or
mixed with authorization telemetry. A failed immediate speed-test submission is
already queued this way by beta clients. `leaderboard_entry` and lifecycle commands
must never be accepted through that queue. The 2026-10-01 receiver fix restores
this compatibility without changing schema or requiring a client update. Pending
queues are retained and resume on the next scheduled retry after server deployment;
do not clear them to work around `route_event_mismatch`.

The production `/exec` URL is a versioned Apps Script deployment. Saving editor code is not enough: after a receiver change, create a new script version and edit the existing deployment to use it. Keep execution as the deploying account and anonymous/public access enabled. A quick contract check is that the root GET reports legacy/default schema 4, `accepted_schemas` contains `[4,5]`, and `GET ?route=leaderboard&limit=3` returns an object with an `entries` array.

The public leaderboard shows **one active position per valid `install_id`**, selecting that installation's winning measurement (download DESC, then upload DESC, lower latency, newer receipt time) and applying its current lifecycle nickname. All displayed metrics come from one winning measurement. Renaming changes that one public position without rewriting measurement history. Different installations may have identical nicknames and appear independently. Historical rows with empty or invalid `install_id` remain separate, since their identities cannot safely be inferred. Deduplication happens only in the public GET view **before sorting, `limit`, and `total`**; raw `Leaderboard` and `SpeedTests` remain append-only. The response exposes only rank, nickname, download/upload, latency, jitter and optional packet loss; private `install_id`, `test_id`, `event_id`, radio metadata and time bucket remain server-side. The companion private Apps Script is deployed manually, retaining the existing `/exec` URL. Its production surface contains only `doGet` and `doPost`; destructive setup, one-time migration, and manual self-test entry points are not deployed.

The private server's offline contract can be run against the owner's exact schema-v4 baseline plus the leaderboard change without accessing production Sheets: `node tests/leaderboard_server_contract.cjs /path/to/updated-private-server.js`.

Also run `node tests/server_ingestion_contract.cjs /path/to/Code.gs` for full
in-memory POST validation, mixed batches, retry/deduplication, route separation and
flush-before-cache/lock-release checks. Both can be run from the private repository
using `./verify.ps1 -ClientRepository /path/to/IntersvyazAutomaticWifi`.
The synthetic `tests/fixtures/queued-speedtest.json` is shared with the C# serializer
and uploader tests; no private source or real user data is included in this repo.

The terminal's optional nickname is a **local `settings.json` preference** (default display: `Гость`), reused on later launches and editable from both rich and compact speed menus. The unsaved guest state cannot publish. A nickname is not a user account or analytical identity; `install_id` remains unchanged when it changes. Publication requires explicit participation/action, independently of anonymous-statistics consent. A saved rename for an active participant is synchronized through leaderboard control, subject to its limits; pending synchronization does not change the public name until accepted.

**Server rollout:** the corresponding standalone schema-v4 Apps Script must be published as a **new version of the existing `/exec` deployment**. It groups historical entries at GET time; both `Leaderboard` and `SpeedTests` remain append-only, and no migration or sheet reset is required.

### Small per-install request limits

The companion receiver now applies the following burst guards before opening
Sheets, with counter updates serialized by its script lock:

| POST route | Requests per `install_id` per 60-second window |
|---|---:|
| `telemetry` | 12 batches, each still limited to 64 events / 64 KiB |
| `speedtest` | 6 |
| `leaderboard` | 6 |
| `leaderboardcontrol` | 30, shared across status/rename/leave/join |

A window starts with the first counted request. Retries count as requests but
deduplication still prevents repeated event writes. A mixed-ID batch counts once
for each distinct ID; all IDs are checked before any counter is charged. This
supports queues retained across local identity changes. The client's maximum
flush of 8 full batches fits within the telemetry allowance. Existing rename and
rejoin limits over 24 hours are unchanged.

Rejections use the existing JSON failure envelope with an optional additive field:
`{"ok":false,"error":"rate_limited","retry_after_seconds":60}`. Apps Script
ContentService still returns JSON over HTTP; do not require an HTTP 429 status.
The delay is the remaining window, rounded up (1–60 seconds). Failure to acquire
the script lock within one second returns the same error with a 2-second delay.
The uploader retains its batch and waits from receipt of the response; missing
delay defaults to 60 seconds. Immediate rate-limited speed statistics are queued,
but leaderboard publications and lifecycle actions are never deferred.

Anonymous leaderboard GETs share a 15-second cache of the top 250 public entries;
`limit` is applied after retrieving this common cache. A miss is rebuilt under
the script lock to avoid concurrent full scans. Successful publication, rename,
leave or join invalidates the cache under the same lock. No identifier is added
to public GET requests. Health responses do not access Sheets.

These are best-effort guards against accidental spam, not a security boundary:
Apps Script may evict cache-backed counters early; generating another `install_id`
still gives another quota. There are deliberately no accounts, access keys,
global write budget or ingestion kill switch. See Google's
[cache lifetime and size limits](https://developers.google.com/apps-script/reference/cache/cache#put(String,String,Integer)).

Legacy/default schema remains 4; path-aware authorization adds schema 5. Prefer updating beta clients before deploying these limits:
older clients can read the error but wait 6 hours on a throttled batch and do not
queue an immediately rate-limited speed result. The new client also works with
the older server. No Sheet migration or `/exec` URL change is required.

Shared fixtures in `tests/fixtures/backend-events.json` plus `queued-speedtest.json`
cover all nine event types in both the production C# serializer and the private
server's validation/storage. `backend-rate-limited.json` is shared by server and
client response/retry tests. These are synthetic examples, not production data.

## Storage model

Google Sheets is an append-only ingestion buffer, not the analytics engine. The sheets correspond to event/entity tables:

- `Attempts`
- `MailboxPolls`
- `InternetProbes`
- `PathObservations`
- `PortalResponses`
- `Errors`
- `RegistrationEvents`
- `SpeedTests`
- `Leaderboard`

The intended analysis path is export/import into SQL, Python, or Parquet. `event_id` is the durable row-level deduplication key; `attempt_id` joins authorization events; `install_id` groups behavior by installation without revealing an InterSvyaz account.

For the network-quality study, `SpeedTests` is the canonical research source. `Leaderboard` retains **every explicitly published entry** as an append-only presentation history, including repeat measurements and nickname changes from one installation. Public `GET leaderboard` collapses those rows by valid `install_id`, without mutating the underlying sheet. Its private `install_id`/`test_id` are retained for attribution and joins but never exposed by the public view.

The public leaderboard response contains rank, nickname, and measurement values only.

## Spreadsheet layout

The schema-4 workbook contains the eight event sheets above plus `LeaderboardActions`, which stores the current leaderboard lifecycle audit trail. `README` documents the privacy contract. All ten tabs are intentional; there are no migration-only sheets. Future schema changes must use explicit non-destructive migrations maintained outside the deployed Web App source.

## Public leaderboard input safety (#13)

The public Google Apps Script leaderboard response is **untrusted input**: anyone can call
its endpoint directly, even if our app validates nickname drafts. `TelemetryClient` now
sanitizes every returned nickname and checks all numeric values **before** constructing
`LeaderboardPublicEntry`, which is the shared model used by rich and compact terminal UI.
The backend must perform the same checks both for newly submitted entries and when
reading legacy/directly edited Google Sheets rows.

The display policy is deliberately conservative: Unicode letters and decimal digits
(including Cyrillic, Latin and CJK), ordinary spaces and `.` `_` `-` are retained.
Other punctuation, emoji/symbols, combining marks, ANSI ESC, C0/C1 controls, carriage
returns, line feeds, Unicode bidi overrides/isolates and zero-width formatting characters
are removed. Consecutive spaces collapse; nicknames are limited to 32 Unicode scalar
values and 256 inspected scalars. Empty sanitized names are excluded. Older nicknames
containing unsupported symbols may be displayed without those symbols.

Rank must be within 1–250 (otherwise a sequential rank is assigned). Down/up speeds
must be 0–10,000 Mbps, ping/jitter 0–60,000 ms, packet loss 0–100%; an invalid
number is displayed as missing. Entries without any valid download, upload or ping
measurement are omitted, and the client never accepts more rows than requested.
The backend must preserve private `install_id`/`test_id` exclusion from public reads.

The Apps Script v4 update for #13 is a **non-destructive code-only update**: edit the
**existing** deployed Web App to a new script version, retaining its `/exec` URL.
Backend source is distributed separately on OneDrive,
so it must be deployed and self-tested separately before closing #13. The client-side
sanitizer is still required even when the server has been updated.
