# Speed tools terminal UI shell

This branch adds only the terminal presentation for the future campus speed-test feature. It intentionally does **not** perform network measurements, persist leaderboard data, or publish results.

## Navigation

The main terminal menu contains `[9] Скорость и рейтинг`. The submenu stays inside the existing left pane so the right live-status pane remains visible.

- `[1] Измерить скорость` — measurement screen with a large block-number area, download/upload/ping summary and start/back actions.
- `[2] Таблица лидеров кампуса` — adaptive leaderboard table shell with refresh/back actions.
- `[3] Никнейм и публикация` — transient nickname editor and publication shell.
- `[0] Назад` / `Esc` — returns without rebuilding the whole terminal framebuffer.

The supplied block-number style is represented by `SpeedMetricGlyphs` in `InteractiveTerminalUi.SpeedTools.cs`; the renderer can accept real numeric text later without changing the layout.

## Backend handoff

The next agent should connect real measurement and leaderboard logic without moving those workflows out of the left pane. The UI currently shows explicit backend-not-connected notices instead of fabricated speed or leaderboard values.

Recommended integration points are the existing shell methods in `InteractiveTerminalUi.SpeedTools.cs`:

- replace the placeholder action in `RunSpeedMeasurementShellAsync` with real progress/result updates;
- populate `RenderLeaderboardFrame` from real campus rows and add scrolling when row count exceeds the visible viewport;
- persist the nickname outside the UI object if desired;
- gate publication on a real completed result and send only after explicit user confirmation.

Keep the existing main-menu selection and framebuffer-preservation behavior intact so returning from the speed submenu only repaints changed cells.
