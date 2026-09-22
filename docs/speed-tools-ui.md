# Speed tools terminal UI

The main terminal menu contains `[9] Скорость и рейтинг`. The screen is connected to the native IS74 LibreSpeed provider and the external Apps Script telemetry/leaderboard backend.

## Navigation and layout

In the normal rich layout the existing two-pane composition is preserved:

- the left pane replaces `МЕНЮ` with `ИЗМЕРЕНИЕ СКОРОСТИ`;
- the right pane replaces `СОСТОЯНИЕ` with `ЛИДЕРЫ КАМПУСА`;
- the normal IS74W banner/subtitle stay visible;
- leaving speed tools returns to the main menu through the preserved framebuffer.

The left pane renders the current/final measurement:

- large download speed in Mbit/s;
- upload speed in Mbit/s;
- ping in milliseconds;
- jitter in milliseconds;
- packet loss when a provider can measure it. The current IS74 LibreSpeed deployment does not, so the value is shown as `—` rather than a fabricated zero.

During a rich-layout measurement the provider reports live latency/download/upload progress. `Esc` cancels the active measurement without affecting Wi-Fi authorization.

The right pane shows public leaderboard rows returned by the backend. `[2]` expands the leaderboard into a dedicated browse view with `↑` / `↓`, `PgUp` / `PgDn`, `Home` / `End` and `R` refresh.

Hotkeys:

- `Enter` / `[1]` — run a real speed measurement against `s.is74.ru`;
- `[2]` — expand/browse the leaderboard;
- `R` — refresh the public leaderboard;
- `[3]` — edit the transient nickname;
- `[4]` — explicitly publish the last completed result;
- `[0]` / `Esc` — return to the normal IS74Wifi menu.

## Data flow

A completed local measurement is converted into the schema-v4 `speed_test` event. If the telemetry endpoint is configured, the app attempts an immediate `route=speedtest` POST. Temporary transport failures are queued locally for the normal deferred telemetry uploader. If no endpoint is configured, measurement still succeeds and its research event stays in the local queue.

Leaderboard publication is separate and opt-in. The user must press `[4]`; the app then creates a `leaderboard_entry` referencing the completed test and sends it through `route=leaderboard`. A temporary transport failure can be queued for retry, but no leaderboard row is created merely by running a speed test.

The public leaderboard is read through `GET ?route=leaderboard&limit=...`. The public response contains only presentation fields such as rank, nickname and measurement metrics. Private `install_id`, `test_id` and `event_id` values are never returned.

The production Google Apps Script endpoint is built into the client. `IS74W_TELEMETRY_URL` or the `TelemetryEndpoint` setting can override it for development/testing.
