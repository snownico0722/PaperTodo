# Anonymous usage statistics

PaperTodo aggregates anonymous usage counters locally and uploads only small completed-day summaries.
The client never uploads paper or to-do text, Markdown/script contents, images, file paths, clipboard contents, usernames, machine names, hardware identifiers, or precise location.

The setting is enabled by default and can be disabled in Advanced Settings under **Anonymous usage statistics / Help improve PaperTodo**. Disabling it clears locally queued unsent reports and stops further collection.

## Delivery

- Only `daily_usage` exists on the wire. The current local day stays local while it is still in progress.
- When PaperTodo next starts on a later local date, the completed day is finalized and uploaded. DAU and related views are therefore intentionally delayed until a later launch.
- If several completed days are queued, one HTTP POST contains the whole backlog; the receiver writes one `daily_usage` row per completed day to CLS.
- If that POST fails or times out, the batch remains local and is retried on a later application launch. PaperTodo does not periodically retry in the background.
- Each daily report has a random-install `install_id` and deterministic `report_id`; no hardware fingerprint is used.
- Network work is asynchronous with a 3-second timeout and never blocks normal PaperTodo behavior.

## Wire fields

The outer request contains `schema_version` and `reports`. Each item in `reports` contains:

`kind`, `schema_version`, `report_id`, `install_id`, `date`, `telemetry_first_seen_date`, `app_version`, `locale`, `country_code`, `country`, `timezone_offset`, `monitor_count`, `launch_count`, `active_seconds`, `paper_count`, `todo_paper_count`, `note_paper_count`, `paper_created`, `paper_deleted`, `todo_created`, `todo_completed`, `pill_enabled`, `pill_count`, `pill_expand`, `pill_collapse`, `markdown_preview`, `image_inserted`, `hotkey_triggered`, `crash_count`.

`country_code` / `country` come from the Windows region setting; PaperTodo does not request location permission.
