# Speed tools terminal UI

The main terminal menu contains `[2] Скорость и рейтинг`. The screen is connected to the native IS74 LibreSpeed provider and the external Apps Script telemetry/leaderboard backend. Current owners: `InteractiveTerminalUi.SpeedTools.cs` in App and `CampusSpeedToolsService` in Core; see [project map](project-map.md) and [checks](checks.md).

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
- packet loss remains part of the telemetry contract for compatible providers, but is not shown in the UI because the current IS74 LibreSpeed deployment does not measure it.

During a rich-layout measurement the provider reports live latency/download/upload progress. `Esc` cancels the active measurement without affecting Wi-Fi authorization.

The right pane shows public leaderboard rows returned by the backend. `[2]` expands the leaderboard into a dedicated browse view with `↑` / `↓`, `PgUp` / `PgDn`, `Home` / `End` and `R` refresh.

Hotkeys:

- `Enter` / `[1]` — run a real speed measurement against `s.is74.ru`;
- `[2]` — expand/browse the leaderboard;
- `R` — refresh the public leaderboard;
- `[3]` — edit the saved local nickname and, for an active participant, synchronize it with the backend subject to rename limits;
- `[4]` — explicitly publish the last completed result;
- `[5]` — leave the public leaderboard, with confirmation;
- `[0]` / `Esc` — return to the normal IS74Wifi menu.

## Data flow

A completed local measurement is converted into the schema-v4 `speed_test` event. Only with anonymous-statistics consent does the app send an immediate `route=speedtest` POST. Temporary failures (including rate limiting) are queued locally for the normal deferred telemetry uploader via `route=telemetry`; a missing endpoint also leaves the event queued. Without consent, measurement still works, but the statistics event is neither sent nor queued.

Leaderboard publication is separate and opt-in; it does not require enabling anonymous statistics. The user must save a nickname (the unsaved guest state cannot publish), press `[4]` and confirm participation when requested. The app creates a `leaderboard_entry` referencing the completed test and sends it through `route=leaderboard`. Publication and participation actions are **never queued**: errors are shown for a deliberate retry. A delayed request must not restore a position after the user has left. No leaderboard row is created merely by running a speed test.

Participation/status, rename, leave and rejoin use `route=leaderboardcontrol`. A later publication after leaving first offers explicit rejoin, subject to the server's 24-hour limit. Renames have a separate limit (3 per 24 hours); a local nickname can remain pending until synchronization succeeds. Anonymous leaderboard reads share a short server cache; `R` requests a refresh, not a cache bypass. See [telemetry contract](telemetry.md) for current limits and response fields.

The public leaderboard is read through `GET ?route=leaderboard&limit=...`. The public response contains only presentation fields such as rank, nickname and measurement metrics. Private `install_id`, `test_id` and `event_id` values are never returned.

The production Google Apps Script endpoint is built into the client. `IS74W_TELEMETRY_URL` or the `TelemetryEndpoint` setting can override it for development/testing.
