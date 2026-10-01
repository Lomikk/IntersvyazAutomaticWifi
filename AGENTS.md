# Repository workflow

- At the end of completed work, run relevant checks and commit the task's changes
  by default, as requested by the owner. Include unrelated existing changes only
  when the owner explicitly asks to commit everything. Report the commit hashes.
- Do not push, publish a release or deploy Apps Script unless explicitly requested.
- Preserve unrelated work. Do not include credentials, real user data or private
  server source in this public repository.
- The companion private server repository is `../IntersvyazAutomaticWifi_SERVER`.
  Its `Code.gs` is verified using `tests/leaderboard_server_contract.cjs` and
  `tests/server_ingestion_contract.cjs`, each taking the server file path.
- See `docs/maintainability-audit-and-plan.md` for the responsibility map and
  staged refactoring plan. A small fix does not authorize the whole plan.
- For App/Core changes, build and run relevant C# contract tests; for UI also run
  `tests/static_checks.py` and `tests/ui_live_status_contract.py`. Report unavailable
  checks. Do not use live Wi-Fi authorization or production writes as routine tests.
