# Anonymous usage statistics

PaperTodo aggregates anonymous usage counters locally and uploads only small summaries.
The client never uploads paper or to-do text, Markdown/script contents, images, file paths, clipboard contents, usernames, machine names, hardware identifiers, or precise location.

The setting is enabled by default and can be disabled in Advanced Settings under **Anonymous usage statistics / Help improve PaperTodo**. Disabling it clears locally queued unsent reports, stops the telemetry timer and input hooks, and stops further collection.

## Delivery

- Only `daily_usage` exists on the wire. `report_stage` distinguishes the one-time `first_seen` report from completed-day `complete` reports.
- On the first telemetry day, if there is no completed previous-day report waiting to send, PaperTodo queues one small `first_seen` report immediately. This prevents a user who tries PaperTodo once and never launches it again from disappearing from the new-user denominator.
- If a completed previous-day/backlog report exists, PaperTodo sends that backlog instead of creating a late `first_seen` report.
- The current local day remains local while it is in progress. When the date rolls over, the completed day is finalized and an upload is attempted immediately; if PaperTodo was not running at rollover, it is finalized on the next launch.
- If several reports are queued, one HTTP POST contains the whole backlog; the receiver writes one JSON row per report to CLS.
- If that POST fails or times out, the batch remains local and is retried on a later application launch or day rollover. PaperTodo does not periodically retry failed network requests.
- Each report uses a random-install `install_id`; no hardware fingerprint is used. `first_seen` uses `<install>_<date>_first_v1`, while the completed day uses `<install>_<date>_v1`.
- Network work is asynchronous with a 3-second timeout and never blocks normal PaperTodo behavior.

## Collection cost

- Windows region, locale, monitor count, app version, and other environment fields are captured when a local day is created instead of being re-read every timer tick.
- The lightweight timer runs every 5 seconds for active-time accounting and day rollover.
- Full paper/todo/note snapshots run at most every 10 seconds while recently active and every 2 minutes while idle.
- Paper create/delete and capsule collapse/expand transitions use lightweight state capture after input plus the timer, so they no longer require rebuilding the full content snapshot every 2 seconds.
- Todo create/complete transitions caused by normal input are captured immediately, with the low-frequency snapshot retained as a fallback for paste/import/programmatic changes.

## Retry deduplication

The SCF receiver is intentionally append-only: a response can be lost after CLS accepted a row, so raw CLS storage is **at-least-once**. Every retry keeps the same deterministic `report_id`, and the receiver writes `received_at_ms`.

Before summing usage metrics, analytics queries must first keep the row with the greatest `received_at_ms` for each `report_id` (for example with CLS `max_by(value, received_at_ms)`). This makes network retries idempotent at the analytics layer without adding a database or device secret to the client.

`first_seen` and `complete` are different report IDs. Use `first_seen` for the new-user denominator; use `complete` for daily usage totals. DAU-style device counts can group by `install_id` + `date` after retry deduplication.

## Wire fields

The outer request contains `schema_version` and `reports`. Each item in `reports` contains:

`kind`, `report_stage`, `schema_version`, `report_id`, `install_id`, `date`, `telemetry_first_seen_date`, `app_version`, `locale`, `country_code`, `country`, `timezone_offset`, `monitor_count`, `launch_count`, `active_seconds`, `paper_count`, `todo_paper_count`, `note_paper_count`, `paper_created`, `paper_deleted`, `todo_created`, `todo_completed`, `pill_enabled`, `pill_count`, `pill_expand`, `pill_collapse`, `markdown_preview`, `image_inserted`, `hotkey_triggered`, `crash_count`.

The receiver additionally writes `received_at` and `received_at_ms`.

`country_code` / `country` come from the Windows region setting; PaperTodo does not request location permission.
