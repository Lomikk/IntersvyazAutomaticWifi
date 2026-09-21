# Speed tools terminal UI shell

This branch adds only the terminal presentation for the future campus speed-test feature. It intentionally does **not** perform network measurements, persist leaderboard data, or publish results.

## Navigation and layout

The main terminal menu contains `[9] Скорость и рейтинг`. In the normal rich layout the existing two-pane composition is preserved:

- the **left pane replaces `МЕНЮ` with `ИЗМЕРЕНИЕ СКОРОСТИ`**;
- the **right pane replaces `СОСТОЯНИЕ` with `ЛИДЕРЫ КАМПУСА`**;
- the normal IS74W banner/subtitle stay visible;
- leaving speed tools returns to the main menu through the preserved framebuffer, so unchanged cells are not redrawn.

The left pane reserves presentation for a future measurement result:

- large download speed in Mbit/s;
- upload speed in Mbit/s;
- ping in milliseconds;
- jitter in milliseconds;
- packet loss in percent.

The right pane reserves presentation for the campus leaderboard, including download/upload/ping/jitter columns when width allows. Narrow layouts drop the upload column first rather than truncating every row beyond readability. The user nickname/result summary and voluntary publication controls live under the table.

Hotkeys:

- `Enter` / `[1]` — start the future speed measurement;
- `[2]` / `R` — refresh the future leaderboard;
- `[3]` — edit the transient nickname;
- `[4]` — publish the future completed result;
- `[0]` / `Esc` — return to the normal IS74Wifi menu.

The supplied block-number style is represented by `SpeedMetricGlyphs` in `InteractiveTerminalUi.SpeedTools.cs`; the renderer can accept real numeric text later without changing the layout.

## Backend handoff

The next agent should connect real measurement and leaderboard logic without restoring the normal Wi-Fi status pane inside speed tools. The UI currently shows explicit backend-not-connected notices instead of fabricated speed or leaderboard values.

A useful measurement model for the UI is:

- `DownloadMbps`
- `UploadMbps`
- `PingMs`
- `JitterMs`
- `PacketLossPercent`

A leaderboard row can expose rank, nickname, download/upload, ping and jitter. Publication should remain opt-in and should only become available after a completed local measurement.

Recommended integration points in `InteractiveTerminalUi.SpeedTools.cs`:

- wire `Enter` / `[1]` to real progress/result updates and feed download text into `DrawLargeSpeedMetric`;
- replace the placeholder metric rows in `DrawSpeedMeasurementPane`;
- populate `DrawSpeedLeaderboardPane` from real campus rows and add scrolling if the visible row count becomes insufficient;
- persist the nickname outside the UI object if desired;
- gate publication on a real completed result and send only after explicit user confirmation.

Keep the existing main-menu selection and framebuffer-preservation behavior intact so entering/leaving speed tools only repaints cells whose content actually changed.
