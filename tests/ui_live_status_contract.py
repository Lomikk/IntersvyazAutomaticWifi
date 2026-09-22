"""Source contract for the live, network-free interactive menu status refresh.

The UI is Windows-only and cannot be exercised in a headless Linux runner.
Windows CI also compiles the app and runs the C# contract suite.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
ui = (root / 'src/IS74Wifi.App/InteractiveTerminalUi.cs').read_text(encoding='utf-8')
program = (root / 'src/IS74Wifi.App/Program.cs').read_text(encoding='utf-8')
notifications = (root / 'src/IS74Wifi.App/WindowsNotificationService.cs').read_text(encoding='utf-8')

menu = ui.split('public async Task<InteractiveMenuAction> RunMenuAsync(', 1)[1].split('\n    public ', 1)[0]
local = program.split('private static InteractiveStatusSnapshot ReadLocalInteractiveStatusSnapshot()', 1)[1].split(
    'private static async Task<InteractiveStatusSnapshot> RefreshInteractiveStatusAsync()', 1
)[0]

assert 'Func<InteractiveStatusSnapshot> readLocalStatus' in menu
assert 'LocalStatusRefreshInterval = TimeSpan.FromSeconds(2)' in ui
assert 'Task.Run(readLocalStatus, cancellationToken)' in menu
assert 'localStatusTask is { IsCompleted: true }' in menu
assert 'status = localStatusTask.Result;' in menu
assert 'refreshedStatusTask is { IsCompleted: true }' in menu
assert 'ReadLocalInteractiveStatusSnapshot).ConfigureAwait(false)' in program
assert 'new RuntimeStateStore(paths, json).Load()' in local
assert 'new SettingsStore(paths, json).Load()' in local
for forbidden in ('ApplicationRuntime.Create', 'ProbeAsync(', 'CheckAsync(', 'TryCheckForUpdatesIfDueAsync'):
    assert forbidden not in local, f'periodic status refresh must remain local: {forbidden}'

# Suppression of desktop notifications while the UI is open is intentional.
publish = notifications.split('public void Publish(AgentNotification notification)', 1)[1].split(
    'internal static NamedSemaphoreLease?', 1
)[0]
assert 'IsInteractiveSessionRunning()' in publish
print('UI live status / interactive notification contract: OK')
