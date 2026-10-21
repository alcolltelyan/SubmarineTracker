# Upstream integration verification

The candidate merges upstream `04c54d770f815cd416bbade1ac5b178b551a32b5`
into fork `c84d745848a5ba65b665526fcd70dbe101fdd4c1`.

## Automated checks

From the repository root on Windows, with .NET 10 and the installed Dalamud API 15 development libraries:

```powershell
dotnet build ./SubmarineTracker/SubmarineTracker.csproj -c Release
python tests/check_sync_preservation.py
dotnet run --project tests/SyncRegression/SyncRegression.csproj -c Release
```

The C# harness executes the built production DLL. It substitutes Dalamud services
and HTTP transport, and uses disposable databases under
`.codex-dotnet/sync-validation/regression`. It deliberately bypasses the cache's
game-sheet initialization, so it cannot validate game-sheet aggregation, hooks,
or rendered UI. It requires the stopped-plugin backups described below. An
alternate Dalamud library location can be supplied with `-p:DalamudLibPath=...`.

The preservation script checks every fork-only and upstream-only file, merges
resource values semantically, checks resource accessor coverage, and accounts
for every remaining configuration/cache difference.

With Go 1.23 installed (the validation toolchain is also available under
`.codex-dotnet/sync-validation/toolchain/go/bin`):

```powershell
go -C Backend/webhookHandler mod download all
go -C Backend/webhookHandler test -v ./...
go -C Backend/webhookHandler build -o ../../.codex-dotnet/sync-validation/webhookHandler.exe .
```

Go tests use local fake HTTP endpoints and never connect to the backend database.
The module's existing policy ignores `go.sum`; dependency downloads generate it locally.

## Required in-game smoke test — pending

The candidate DLL is `SubmarineTracker/bin/Release/SubmarineTracker.dll`
(version `2.0.6.3`), the development-plugin path recorded in the local Dalamud
configuration and loading log. No game or launcher was running when the backup
and builds were performed. The agent has not loaded the candidate in-game.

1. Load that development plugin and confirm existing FCs, submarines, voyage
   history, custom profiles, notification settings, and custom hours are present.
2. In `/sloot`, select **Custom (Hours)**. Check the saved value, the minimum of
   one hour, filtering with multiple hour values, and five-second refresh after
   processing new loot. Verify existing date presets still work.
3. With your configured Discord destination, process a voyage. Confirm the gil
   total matches the selected custom profile and only one report appears for
   that result. Confirm the dispatch/return notifications you use still work.
4. In configuration's **Manage** tab, create a temporary account, assign an FC,
   check selection/grouping, and remove the temporary account. Existing FC data
   should remain available.
5. Check inventory/storage counts and resource/leftover display in the main
   overview and Helpy. Check behavior with the Allagan Tools integration you use.
6. Restart the plugin/game and confirm settings, voyage history, accounts, and
   storage persist without migration or plugin errors.

Report pass/fail for each item and any relevant error. Local `master` must not
advance until these checks pass. If the user changes their verification
requirements, record that explicitly before changing the gate.

## Recovery artifacts

- Original source reference: `codex/backup-before-upstream-20261020`.
- Verified complete-history bundle: `.codex-dotnet/sync-validation/before-upstream.bundle`.
- Stopped-plugin configuration and data: `.codex-dotnet/sync-validation/pre-runtime/`.
- Original Release output: `.codex-dotnet/sync-validation/baseline-build/`.

The persisted-data backup contains `SubmarineTracker.json`, the entire
`SubmarineTracker` configuration directory, the older root-level database under
the name `legacy-root-submarine-sqlite.db`, and `sha256.json` recording file hashes.
These ignored files contain private configuration and must stay local.

The actual paths found on this machine are:

- `%APPDATA%/XIVLauncher/pluginConfigs/SubmarineTracker.json`
- `%APPDATA%/XIVLauncher/pluginConfigs/SubmarineTracker/submarine-sqlite.db`
- `%APPDATA%/XIVLauncher/pluginConfigs/submarine-sqlite.db` (older legacy database)

If runtime testing fails, stop the plugin/game. Preserve the failed-test state
separately, then restore the matching original config directory, JSON file,
and original Release output from these backups. Restore the DB together with
its matching journal files; do not pair a restored DB with post-test WAL/SHM
files. Keep the original backup intact. No live data was modified by the
automated checks.
