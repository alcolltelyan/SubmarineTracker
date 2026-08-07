# Fork Updates

This document records the fork-owned changes related to custom loot timeframes, gil-value Discord reporting, and Discord webhook reliability. It is based on the code and Git history in this repository, compared with the common upstream baseline at commit `4351c4e` (`Infiziert90/SubmarineTracker`). It does not describe later upstream-only commits or unrelated inherited features.

## Fork commit scope

| Commit | Date | Change |
| --- | --- | --- |
| `efbc832` | 2026-03-16 | Added the custom-hours loot date filter and localized UI strings. |
| `fd550ee` | 2026-03-16 | Reduced loot data and custom-profile result cache refresh intervals from 30 seconds to 5 seconds. |
| `05b3caf` | 2026-03-16 | Added loot-processed Discord notifications with a selectable custom loot value profile. |
| `902ff86` | 2026-05-08 | Added outbound throttling, bounded Discord rate-limit retries, and stronger response handling to both webhook clients. |

At the upstream baseline, custom loot value profiles already existed. This fork reuses those profiles for the new Discord gil report; it does not introduce the profile editor or the underlying item-price dictionary.

## Custom loot timeframe

### User-facing behavior

The Custom Loot window's date-limit selector now includes `Custom (Hours)`. Selecting it reveals an `Hours:` integer input beneath the selector.

- The persisted setting is `Configuration.CustomLootHours`.
- The default is `12` hours.
- Values are clamped to a minimum of `1` hour. There is no code-enforced maximum.
- The input's normal step is 1 hour and its fast step is 12 hours.
- Changing the value saves the plugin configuration immediately and invalidates the current loot result cache.
- The option and input labels are localized in English, German, French, Japanese, and Chinese resources.

The new enum member is `DateLimit.CustomHours = 99`. Using an explicit value of `99` keeps it separate from the existing sequential preset values (`None`, day, week, month, and year ranges).

### Filtering semantics

`DateUtil.ToDate()` calculates the lower boundary as:

```text
DateTime.UtcNow - max(1, CustomLootHours) hours
```

When `CustomHours` is active, a loot record is included when its date is greater than or equal to that UTC boundary. The boundary is recalculated whenever the date comparison runs, so the window is rolling rather than anchored to the time at which the option was selected.

This differs from `DateLimit.None`, which retains the inherited explicit From/To date-picker behavior.

### Refresh behavior

The fork reduces two related refresh intervals from 30 seconds to 5 seconds:

- `DatabaseCache.LongDelay`, which controls refresh checks for cached loot data.
- `LootWindow.Custom.BuildCache()`, which controls rebuilding the custom-profile aggregation displayed in the loot window.

Changing the selected date limit or custom hour value still sets `LastRefreshTime` to zero, requesting a rebuild on the next eligible frame. The shorter periodic interval also makes newly processed loot and profile value changes appear in the custom loot view sooner.

### Files changed

- `SubmarineTracker/Configuration.cs`
- `SubmarineTracker/Data/DateLimit.cs`
- `SubmarineTracker/DatabaseCache.cs`
- `SubmarineTracker/Windows/Loot/LootWindow.Custom.cs`
- `SubmarineTracker/Resources/Language*.resx`
- `SubmarineTracker/Resources/Language.Designer.cs`

## Loot-processed gil Discord reporting

### Configuration and UI

Two persisted settings were added:

| Setting | Default | Purpose |
| --- | --- | --- |
| `WebhookLootProcessed` | `false` | Enables the new notification after voyage loot is processed. |
| `WebhookLootProcessedProfile` | `"Default"` | Names the custom loot value profile used for the gil calculation. |

The Notifications configuration window adds a `Send Loot Processed` checkbox. When enabled, it displays a `Loot Value Profile` combo populated from the keys of `Configuration.CustomLootProfiles`.

If the configured profile name no longer exists, the UI sets the combo index to zero, which displays the first dictionary key when at least one profile exists. This display fallback does not rewrite `WebhookLootProcessedProfile` unless the user changes the combo. During report generation, the calculation separately falls back to the profile named `Default`. If `Default` is also absent, an empty price dictionary is used and the calculated value is zero.

The two new notification controls are currently hard-coded English strings; unlike the custom timeframe labels, they do not have localization resource entries.

### Trigger and data source

The report is initiated from `HookManager.PacketReceiver()` while processing the existing voyage-results event:

1. The original game packet handler is called first.
2. Only event ID `721343` is processed.
3. The housing manager, workshop territory, and current submersible data pointer must be available.
4. The first gathered result must contain a primary item.
5. Gathered sectors are filtered to entries whose `Point` is greater than zero.
6. The existing build/rank reconstruction and `Loot` model creation run as before.
7. When `WebhookLootProcessed` is enabled, Discord report generation is queued on a background task. Database insertion is queued separately.

The notification therefore reports from the packet-derived `lootList`; it does not wait for the asynchronous database insert to complete and does not query the database to reconstruct the voyage loot.

### Send conditions and duplicate prevention

The background report exits without sending unless all of these conditions are met:

- The client is logged in.
- Discord offline mode is disabled.
- The configured webhook URL starts with `https://`.
- The process can immediately acquire the named Windows mutex `Global\\SubmarineTrackerMutex`.
- The packet produced at least one valid loot entry.
- The in-memory `SentLootProcessed` set has not already recorded the key `LootProcessed{fcId}{register}{returnTime}`.
- The matching submarine and free-company records can be found in `DatabaseCache`.

The named mutex follows the existing multibox suppression approach used by return notifications. The hash set prevents duplicate reports within the current plugin process lifetime; it is not persisted across plugin or game restarts.

Loot-processed reporting is intentionally skipped in offline mode. The backend offline-return webhook worker does not generate this report.

### Gil calculation

The selected custom loot profile is a dictionary of item ID to per-item gil value. For each `Loot` entry, the report calculates:

```text
total += primary item count * configured primary item value
total += additional item count * configured additional item value
```

The additional item is counted only when `Loot.ValidAdditional` is true. Items absent from the selected profile contribute zero. Multiplication is promoted to `long`, and the accumulated `totalValue` is also a `long`.

This is a configured valuation, not a live market-board lookup. The displayed amount is determined entirely by the values stored in the selected custom loot profile at report-generation time.

### Discord message format

The report sends one embed through the shared plugin webhook client:

- Title: the existing formatted submarine name from `NameConverter.GetSub(sub, fc)`.
- Description: `Loot processed. Total value: {totalValue:N0} gil (Profile: {profileName}).`
- Embed color: decimal `11027200`.
- Webhook username and avatar: the existing `[Submarine Tracker]` identity and project icon configured by `Webhook.WebhookContent`.

The number uses the runtime's `N0` formatting, so it has no decimal places and uses the active culture's thousands separator. The description itself is currently English-only.

The loot-processed message contains only an embed. It does not add the configured user or role mention fields used by offline return notifications.

### Files changed

- `SubmarineTracker/Configuration.cs`
- `SubmarineTracker/Manager/HookManager.cs`
- `SubmarineTracker/Windows/Config/ConfigWindow.Notify.cs`

## Discord webhook reliability

The upstream baseline had two different behaviors:

- The in-game C# client sent once and logged non-success responses; it did not retry HTTP 429 responses.
- The Go offline-return worker parsed `retry_after`, slept, and recursively called `SendWebhook()` with no retry limit.

The fork replaces both behaviors with throttled, bounded retry handling.

### In-game C# webhook client

All plugin-side webhook types that call `Webhook.PostMessage()` now share one serialized outbound path. This includes dispatch, return, and loot-processed messages.

- A static `SemaphoreSlim(1, 1)` allows only one send operation, including its retries, to run at a time.
- Successful or terminal requests establish a minimum 500 ms delay before the next queued send.
- The JSON payload is serialized once and reused for retries.
- HTTP responses and request content are disposed after each attempt.
- Any successful HTTP status ends the operation.
- HTTP 429 is retried at most three times, for a maximum of four total attempts.
- `retry_after` is interpreted as seconds, converted to milliseconds, rounded upward, and given an additional 250 ms safety buffer.
- If the 429 response cannot be parsed as JSON, the retry delay falls back to 1 second.
- While waiting after a 429, the sender retains the semaphore, so later plugin webhook messages remain queued behind the rate-limited request.
- Exhausted retries and other non-success responses log the status and response body. Exceptions are logged as `Webhook post failed`.

The parsed Discord response model includes `message`, `retry_after`, `global`, and optional `code`. Scheduling is driven by `retry_after`; the `global` and `code` fields are retained for response compatibility and diagnostics but do not select a different retry path.

### Go offline-return webhook worker

The backend worker throttles independently for each webhook URL so one destination does not delay unrelated destinations.

- A mutex protects a map from webhook URL to its next scheduled send time.
- Each attempt uses the current map value to reserve a scheduled time, advances the stored value by 500 ms, releases the mutex, and then sleeps until its reservation.
- Normal concurrent reservations for the same URL are therefore spaced by 500 ms, while goroutines for different URLs wait independently.
- The JSON payload is marshaled once and reused through `bytes.NewReader()` for each attempt.
- HTTP 429 is retried at most three times, for a maximum of four total attempts.
- The worker waits for Discord's `retry_after` plus a 250 ms safety buffer before the next attempt for that URL.
- A retryable 429 replaces that URL's stored timestamp with `now + retry delay`; success and terminal error paths replace it with `now + 500 ms`.
- HTTP 200 and 204 are treated as success, matching the worker's original accepted statuses.
- Response bodies are closed on success, rate limit, and error paths.
- Retry exhaustion reports the number of retries and Discord's message; other error responses retain their response-body logging.

The throttle map is in memory and is reset when the backend process restarts.

### Files changed

- `SubmarineTracker/Webhook.cs`
- `Backend/webhookHandler/handler.go`

## Operational summary

The fork's Discord flow is now:

```text
voyage result packet
    -> build loot entries
    -> calculate configured-profile gil value
    -> create loot-processed embed
    -> enter shared outbound throttle
    -> send
    -> on HTTP 429, wait retry_after + 250 ms
    -> retry up to three times
```

Return notifications created by the offline backend use the same bounded retry policy and a per-webhook 500 ms reservation schedule. Existing dispatch and return messages sent by the plugin also benefit from the new shared C# throttle even though their message content was not otherwise changed by these fork commits.
