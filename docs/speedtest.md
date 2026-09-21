# Campus Wi-Fi speed test core

## Provider choice

The native speed-test core targets the provider's own `https://s.is74.ru/` service. A HAR capture taken on 2026-09-21 shows that this site is a standalone LibreSpeed deployment, so no external CDN or third-party speed-test service is required.

The HAR itself is intentionally not committed because it contains request metadata including the client's public IP. Only the protocol facts required by the implementation are recorded here.

## Observed provider protocol

The captured page loads `speedtest.js` and `speedtest_worker.js` from `s.is74.ru`; `speedtest.js` identifies the deployed engine as LibreSpeed v5.4.1. The server list is empty, so the worker uses the same origin for all measurement traffic.

The deployed worker defaults and the observed Chromium run are:

- test order: `IP_D_U` (`IP`, one-second pause, download, one-second pause, upload);
- ping/jitter: 10 sequential `GET /backend/empty.php?r=...` requests;
- download: 5 Chromium-quirk streams, staggered by 300 ms, using `GET /backend/garbage.php?r=...&ckSize=100`;
- upload: 3 streams, staggered by 300 ms, using `POST /backend/empty.php?r=...`;
- download grace period: 1.5 s;
- upload grace period: 3 s;
- maximum measured throughput duration: 15 s, automatically shortened on fast links;
- throughput status update interval: 200 ms;
- transport-overhead compensation: 1.06;
- upload body size: 20 MiB per request;
- bandwidth unit: decimal Mbit/s, not Mibit/s.

In the capture, the five download streams end together when the timed phase is cancelled. Upload requests can receive HTTP 413 after transmitting their body; LibreSpeed still counts bytes successfully pushed by the client and restarts the stream. The native implementation preserves that behavior and does not treat the response status alone as loss of already-transmitted upload bytes.

## Latency and jitter compatibility

The first ping is a warm-up and is excluded from the reported latency. The remaining pings produce:

- latency: minimum observed request-to-response-header time;
- jitter: absolute delta between adjacent pings, smoothed with LibreSpeed's asymmetric weighted update (larger spikes receive more weight).

The deployed worker does not measure packet loss. `packet_loss_pct` therefore remains absent/null instead of being falsely reported as zero.

## Native implementation choices

`Is74SpeedTestProvider` reproduces the provider measurement traffic without embedding or executing the website JavaScript. It intentionally omits two browser-page actions that are irrelevant to speed measurement:

- `GET /backend/getIP.php` — the app does not need to learn or persist the public IP;
- `POST /results/telemetry.php` — the app does not submit results into the provider site's telemetry system.

The core exposes `IProgress<SpeedTestProgress>` and cancellation so the separate terminal UI branch can render a live speedometer and stop a test cleanly. Authorization does not call the speed-test provider, so these high-bandwidth requests cannot enter the captive-authorization critical path.

`HttpClientProfiles.CreateSpeedTestClient()` allows up to eight same-origin connections, enough for the observed five download streams plus control traffic.

## Application telemetry mapping

A completed measurement can be converted with `SpeedTestTelemetry.CreateEvent(...)` into the schema-v3 `speed_test` row. The event is classified as:

- `test_scope = regional`;
- `server_kind = regional_provider`;
- `test_version = is74-librespeed-5.4.1-v1`.

The event includes measured download/upload Mbit/s, latency, jitter, phase durations, transferred byte counts and the number of latency samples used. `WindowsWifiService.GetSpeedTestRadioSnapshot()` can additionally attach a coarse signal bucket, campus-vs-other Wi-Fi classification, link Rx/Tx rates, and 802.11 PHY generation. Windows WLAN signal quality is bucketed as `excellent >= 80`, `good >= 60`, `fair >= 40`, otherwise `poor`. Wi-Fi band remains `unknown` until we add a reliable frequency/channel source; guessing 2.4/5/6 GHz from PHY alone would corrupt research data.

## Backend boundary

The external Apps Script is not part of this public repository. Its current schema-v3 receiver accepts `speed_test` and `leaderboard_entry` as ordinary JSON POST events on the same web-app endpoint used for authorization telemetry. The application repository contains only the client-side DTO/wire contract, not the script, spreadsheet ID, deployment URL, or credentials.
