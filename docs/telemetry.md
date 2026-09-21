# Telemetry: local-first authorization traces

## Goal

Telemetry exists to measure the real captive-authorization race without changing it. The most important production question is whether the latency-first path behaves on real machines as designed:

1. `POST /stepOne` starts at `T=0`.
2. `/mobile/pushmessages` GETs start at absolute schedule slots without waiting for earlier GET responses.
3. A fresh Wi-Fi code may be observed while `/stepOne` is still pending.
4. `POST /stepTwo` starts immediately after the fresh code, even when `/stepOne` has not completed.
5. Internet is confirmed independently through the strict `online.susu.ru` probe.

The telemetry implementation therefore records precise local timing first and performs no telemetry HTTP requests inside the authorization critical path. Collection and upload are opt-in: after registration the interactive client asks once for permission to send anonymous application statistics. Declining is remembered and is not asked again on ordinary startup; the preference remains available in Settings.

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

## Local queue

When anonymous statistics are allowed, after the network authorization work has finished the completed trace is serialized into a unique JSONL file under:

```text
%LOCALAPPDATA%\IS74Wifi\telemetry\pending\
```

One file represents one completed authorization trace and contains the attempt summary plus its detailed poll/probe/portal/error events. Files are append-only from the analytics point of view and are never modified after publication to the queue.

The queue is bounded (currently about 10 MiB / 1024 trace files). An individually invalid/oversized trace is quarantined rather than blocking later uploads.

A transport batch groups whole trace files, up to 64 events and roughly 60 KiB. The client intentionally stays below the currently deployed Apps Script guards (64 events / 64 KiB) so JSON-envelope overhead cannot push an otherwise valid batch over the receiver limit. `batch_id` is deterministic from the serialized event payload. If an HTTP response is lost, the exact same local data therefore produces the same batch ID on retry. Each row also has its own `event_id` for durable downstream deduplication.

## Upload scheduling

Authorization traces are intentionally not uploaded in real time. Upload scheduling is active only while anonymous statistics consent is `allowed`. The background process tries to flush telemetry only when its next authorization wake-up is at least one minute away. The default upload interval is 12 hours.

By default a flush sends one bounded batch. This keeps normal operation close to the intended “accumulate locally, upload rarely” model. The setting can raise that cap for recovery/backlog draining; if data remains after a successful flush, the next attempt is delayed by six hours. HTTP timeout is 3 seconds by default. Failures leave files in the local queue and use backoff; authorization never depends on upload success.

The production HTTPS telemetry endpoint is built into the client. `IS74W_TELEMETRY_URL` or the `TelemetryEndpoint` setting can override it for development/testing. Transport failures keep data in the local queue and never block authorization.

The Google client uses the Windows system proxy configuration. Normal requests write a bounded final response preview, elapsed time, HTTP status/version, and a classified failure (`dns`, `connect`, `tls`, `proxy`, `redirect`, `protocol`, `timeout`, or caller `cancelled`) to `diagnostic.log`. Interactive UI calls use their own 15-second budget by default; the background uploader keeps its independent short timeout.

For an end-to-end Windows trace, run:

```powershell
IS74Wifi.exe backend-diagnose
IS74Wifi.exe backend-diagnose --post
```

The first command performs DNS resolution, manually displays every GET redirect and final body, then repeats the leaderboard read through the production `TelemetryClient`. `--post` additionally sends an invalid empty speed-test payload: it exercises `doPost` and the expected POST-to-GET ContentService redirect without appending a sheet row.

## Ingestion API contract

The Apps Script source is intentionally kept outside this public repository. The updated schema-v3 receiver keeps the original generic POST contract for backward compatibility and adds explicit routes for user-triggered speed features:

```text
POST <web-app endpoint>                         # legacy/mixed queued batch
POST <web-app endpoint>?route=speedtest         # one completed speed_test
POST <web-app endpoint>?route=leaderboard       # one explicit leaderboard_entry
GET  <web-app endpoint>?route=leaderboard&limit=100
```

The payload `event_type` still selects the row schema (`attempt`, `mailbox_poll`, `internet_probe`, `portal_response`, `error`, `speed_test`, or `leaderboard_entry`). Authorization traces use a `{batch_id, events[]}` envelope. The receiver guards each request at 64 events / 64 KiB, which is why the local queue emits at most 64 events and targets roughly 60 KiB per transport batch.

The production `/exec` URL is a versioned Apps Script deployment. Saving editor code is not enough: after a receiver change, create a new script version and edit the existing deployment to use it. Keep execution as the deploying account and anonymous/public access enabled. A quick contract check is that the root GET reports schema 3 and `GET ?route=leaderboard&limit=3` returns an object with an `entries` array.

The public leaderboard is sorted by download speed, then upload speed, then lower latency, with newest rows as the final tie-breaker. Its response exposes only rank, nickname, download/upload, latency, jitter and optional packet loss. Private `install_id`, `test_id`, `event_id`, radio metadata and time bucket remain server-side.

No route-specific rate limits, accepted-tests-per-day limit, or ingestion kill switch are enforced yet. Those remain possible backend hardening work. Because the client is open source, future anti-abuse controls should be treated as operational guards rather than an identity/security boundary.

## Storage model

Google Sheets is an append-only ingestion buffer, not the analytics engine. The sheets correspond to event/entity tables:

- `Attempts`
- `MailboxPolls`
- `InternetProbes`
- `PortalResponses`
- `Errors`
- `SpeedTests`
- `Leaderboard`

The intended analysis path is export/import into SQL, Python, or Parquet. `event_id` is the durable row-level deduplication key; `attempt_id` joins authorization events; `install_id` groups behavior by installation without revealing an InterSvyaz account.

For the network-quality study, `SpeedTests` is the canonical research source. `Leaderboard` is only a recreational presentation dataset and may contain many nicknames/entries for one installation. Its private `install_id`/`test_id` are retained for attribution and joins but must never be exposed by `GET leaderboard`.

The public leaderboard response contains rank, nickname, and measurement values only.

## Spreadsheet setup

While the spreadsheet contains test data only, `setupSheets()` is intentionally a destructive development reset for the telemetry sheets. It clears old test rows and schema remnants and writes the exact current headers.

Once production telemetry starts, `setupSheets()` must no longer be run. Future schema changes must use explicit non-destructive migrations.
