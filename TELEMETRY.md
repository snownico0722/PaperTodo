# Anonymous usage statistics

PaperTodo aggregates anonymous usage counters locally and uploads only small daily summaries.
The client never uploads paper or to-do text, Markdown/script contents, images, file paths, clipboard contents, usernames, machine names, hardware identifiers, or precise location.

The setting is enabled by default and can be disabled in Advanced Settings under **Anonymous usage statistics / Help improve PaperTodo**. Disabling it clears locally queued unsent reports and stops further collection.

## Delivery

- `daily_presence`: queued once per local day so DAU, new-user, version, language and country/region views are available without waiting for the next day.
- `daily_usage`: the completed local-day aggregate, queued when the date rolls over and retried on later launches if offline.
- Each report has a random-install `install_id` and a deterministic `report_id`; no hardware fingerprint is used.
- Network work is asynchronous with a 3-second timeout and never blocks normal PaperTodo behavior.

## Wire fields

`kind`, `schema_version`, `report_id`, `install_id`, `date`, `telemetry_first_seen_date`, `app_version`, `locale`, `country_code`, `country`, `timezone_offset`, `monitor_count`, `launch_count`, `active_seconds`, `paper_count`, `todo_paper_count`, `note_paper_count`, `paper_created`, `paper_deleted`, `todo_created`, `todo_completed`, `pill_enabled`, `pill_count`, `pill_expand`, `pill_collapse`, `markdown_preview`, `image_inserted`, `hotkey_triggered`, `crash_count`.

`country_code` / `country` come from the Windows region setting; PaperTodo does not request location permission.
