# Speed tools terminal UI shell

This branch adds only the terminal presentation for the future campus speed-test feature. It intentionally does **not** perform network measurements, persist leaderboard data, or publish results.

## Navigation

The main terminal menu contains `[9] Скорость и рейтинг`. Speed tools switch into their own full-width terminal workspace: the normal right-side status pane and large Wi-Fi banner are intentionally omitted so the measurement and campus data can use the whole 120-column target area. The regular main UI returns through the preserved framebuffer when the user leaves speed tools.

The feature is split into two pages instead of nested submenus:

- `[1] ЗАМЕР` — full-width measurement page with a large block-number speed area, download/upload/ping summary and `Enter` to start the future measurement.
- `[2] РЕЙТИНГ` — full-width campus leaderboard page. It also contains the user's nickname/result summary and the voluntary publication actions.
- `←` / `→` or `1` / `2` — switch pages.
- On the rating page: `[3]` edits the transient nickname, `[4]` opens the publication placeholder, `Enter` / `R` refreshes the future leaderboard.
- `[0]` / `Esc` — returns to the normal IS74Wifi menu without rebuilding the whole terminal framebuffer.

The supplied block-number style is represented by `SpeedMetricGlyphs` in `InteractiveTerminalUi.SpeedTools.cs`; the renderer can accept real numeric text later without changing the layout.

## Backend handoff

The next agent should connect real measurement and leaderboard logic without reintroducing the normal Wi-Fi status pane inside speed tools. The UI currently shows explicit backend-not-connected notices instead of fabricated speed or leaderboard values.

Recommended integration points in `InteractiveTerminalUi.SpeedTools.cs`:

- wire the `Enter` action on the measurement page to real progress/result updates and feed numeric text into `DrawLargeSpeedMetric`;
- populate `RenderLeaderboardFrame` from real campus rows and add scrolling when row count exceeds the visible viewport;
- persist the nickname outside the UI object if desired;
- gate publication on a real completed result and send only after explicit user confirmation.

Keep the existing main-menu selection and framebuffer-preservation behavior intact so entering/leaving speed tools only repaints cells whose content actually changed.
