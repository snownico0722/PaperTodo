# Anonymous usage statistics

PaperTodo aggregates anonymous usage counters locally and uploads only small summaries.
The client never uploads paper or to-do text, Markdown/script contents, images, file paths, clipboard contents, usernames, machine names, hardware identifiers, or precise location.

The setting is enabled by default and can be disabled in Advanced Settings under **Anonymous usage statistics / Help improve PaperTodo**. Disabling it clears locally queued unsent reports, stops the telemetry timer and input hooks, and stops further collection.

## Delivery

- Only `daily_usage` exists on the wire. There is no separate presence/new-user event type.
- On a brand-new telemetry install, if there is no completed previous-day/backlog report waiting to send, PaperTodo sends one provisional row for the current local day immediately. This prevents a user who tries PaperTodo once and never launches it again from disappearing from the new-user denominator.
- The provisional row uses exactly the same deterministic `report_id` as that day's eventual completed row: `<install_id>_<date>_v1`.
- If a completed previous-day/backlog report exists, PaperTodo sends that backlog instead of creating a provisional current-day row.
- The current local day otherwise remains local while it is in progress. When the date rolls over, the completed day is finalized and an upload is attempted immediately; if PaperTodo was not running at rollover, it is finalized on the next launch.
- If the provisional row was already accepted, the later completed row is another raw CLS row with the same `report_id`. Retry deduplication therefore also upgrades the provisional row to the final row automatically.
- If several reports are queued, one HTTP POST contains the whole backlog; the receiver writes one JSON row per report to CLS.
- If that POST fails or times out, the batch remains local and is retried on a later application launch or day rollover. PaperTodo does not periodically retry failed network requests.
- Each report has a random-install `install_id`; no hardware fingerprint is used.
- Network work is asynchronous with a 3-second timeout and never blocks normal PaperTodo behavior.

## Collection cost

- Windows region, locale, monitor count, app version, and other environment fields are captured when a local day is created instead of being re-read every timer tick.
- The lightweight timer runs every 5 seconds for active-time accounting and day rollover.
- Full paper/todo/note snapshots run at most every 10 seconds while recently active and every 2 minutes while idle.
- Paper create/delete and capsule collapse/expand transitions use lightweight post-input state capture plus the timer, so they no longer require rebuilding the full content snapshot every 2 seconds.
- Todo create/complete transitions caused by normal input are captured after WPF input processing, with the low-frequency snapshot retained as a fallback for paste/import/programmatic changes.
- Todo completion remembers the item/text state before the click, so an immediately auto-cleared completed row is still counted even if the visual row disappears before post-input processing.

## Retry deduplication

The SCF receiver is intentionally append-only: a response can be lost after CLS accepted a row, so raw CLS storage is **at-least-once**. Every retry keeps the same deterministic `report_id`, and the receiver writes `received_at_ms`.

Before summing usage metrics or counting installs, analytics queries must first keep the row with the greatest `received_at_ms` for each `report_id` (for example with CLS `max_by(value, received_at_ms)`). The same rule handles both ordinary network retries and the first-day provisional → completed-row replacement without adding a database or device secret.

After this retry deduplication, `telemetry_first_seen_date` can be used for new-user cohorts, while `install_id` + `date` can be used for DAU-style device counts.

## Wire fields

The outer request contains `schema_version` and `reports`. Each item in `reports` contains:

`kind`, `schema_version`, `report_id`, `install_id`, `date`, `telemetry_first_seen_date`, `app_version`, `locale`, `country_code`, `country`, `timezone_offset`, `monitor_count`, `launch_count`, `active_seconds`, `paper_count`, `todo_paper_count`, `note_paper_count`, `paper_created`, `paper_deleted`, `todo_created`, `todo_completed`, `pill_enabled`, `pill_count`, `pill_expand`, `pill_collapse`, `markdown_preview`, `image_inserted`, `hotkey_triggered`, `crash_count`.

The receiver additionally writes `received_at` and `received_at_ms`.

`country_code` / `country` come from the Windows region setting; PaperTodo does not request location permission.
