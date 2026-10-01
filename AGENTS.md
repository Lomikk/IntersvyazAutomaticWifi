# Repository workflow

- Start with `docs/project-map.md` for current owners and `docs/checks.md` for
  verification. `docs/maintainability-audit-and-plan.md` describes future stages,
  not the current file layout. Execute only the requested stage/fix.
- Update the affected current docs when behavior/ownership changes; for a named
  plan stage, update its execution registry when the stage is actually complete.
- The active beta runtime is C# in `src/IS74Wifi.App` and `src/IS74Wifi.Core`.
  Root PowerShell scripts/module are a retained reference, not the default place
  for new features. Experiments are not routine tests.
- Keep console input/rendering in App; keep protocol/timing rules in Core.
  Authorization progress callbacks must not do console I/O or block the flow.
  Do not change wire schema, consent, polling timings or persistence as a side
  effect of a UI/refactoring task. No new framework/layer just for symmetry.
- One check command per scope (PowerShell 7, from repo root):
  `./scripts/check.ps1 -Mode App` for UI; `-Mode Core` for Core;
  `-Mode Backend -ServerPath ../IntersvyazAutomaticWifi_SERVER/Code.gs` for server;
  `-Mode Full -ServerPath ../IntersvyazAutomaticWifi_SERVER/Code.gs` for both.
  Without private source, explicitly use `-Mode Full -SkipBackend` and report
  that the server was NOT verified. Cross-boundary/wire changes need Full + server.
  Changes to the wrapper/CI need Full (including check-runner contracts).
- At the end of completed work, run relevant checks and commit the task's changes
  by default, as requested by the owner. Include unrelated existing changes only
  when the owner explicitly asks to commit everything. Report the commit hashes.
- Do not push, publish a release or deploy Apps Script unless explicitly requested.
- Preserve unrelated work. Do not include credentials, real user data or private
  server source in this public repository.
- The companion private server repository is `../IntersvyazAutomaticWifi_SERVER`.
  Its `Code.gs` is verified using `tests/leaderboard_server_contract.cjs` and
  `tests/server_ingestion_contract.cjs`, each taking the server file path.
- Report unavailable checks; a build is not a manual UI or field test. Do not use
  live Wi-Fi authorization or production writes as routine tests. Do not launch
  install/uninstall/agent as a local smoke test on the owner's real state.
