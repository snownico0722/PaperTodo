# CLS telemetry queries

PaperTodo telemetry ingestion is append-only. A network retry can therefore leave more than one raw CLS row with the same deterministic `report_id`.

Do not aggregate raw rows directly. Deduplicate by `report_id` first, keeping values from the row with the greatest `received_at_ms` via `max_by(value, received_at_ms)`, then aggregate the deduplicated result.

These examples use syntax supported by Tencent CLS: nested subqueries, `group by`, `count(distinct ...)`, and `max_by(x, y)`.

## Required indexes

Enable key-value indexes and statistics for fields used by the queries, especially:

- `report_id`
- `report_stage`
- `install_id`
- `date`
- `received_at_ms`
- every numeric metric that will be aggregated

## New telemetry users per day

`first_seen` is emitted once on the first telemetry day when no completed previous-day report is waiting to send.

```sql
report_stage:first_seen |
SELECT date, COUNT(*) AS new_users
FROM (
    SELECT
        report_id,
        MAX_BY(date, received_at_ms) AS date
    GROUP BY report_id
)
GROUP BY date
ORDER BY date
LIMIT 1000
```

## Daily active installs

Both `first_seen` and `complete` can exist for the same install/date, so count distinct installs after retry deduplication.

```sql
* |
SELECT date, COUNT(DISTINCT install_id) AS dau
FROM (
    SELECT
        report_id,
        MAX_BY(date, received_at_ms) AS date,
        MAX_BY(install_id, received_at_ms) AS install_id
    GROUP BY report_id
)
GROUP BY date
ORDER BY date
LIMIT 1000
```

## Completed-day usage totals

Always filter to `report_stage:complete` for usage counters. This example shows the pattern; add other counters the same way.

```sql
report_stage:complete |
SELECT
    date,
    SUM(active_seconds) AS active_seconds,
    SUM(paper_created) AS paper_created,
    SUM(todo_created) AS todo_created,
    SUM(todo_completed) AS todo_completed,
    SUM(pill_expand) AS pill_expand,
    SUM(pill_collapse) AS pill_collapse,
    SUM(markdown_preview) AS markdown_preview,
    SUM(image_inserted) AS image_inserted,
    SUM(hotkey_triggered) AS hotkey_triggered,
    SUM(crash_count) AS crash_count
FROM (
    SELECT
        report_id,
        MAX_BY(date, received_at_ms) AS date,
        MAX_BY(active_seconds, received_at_ms) AS active_seconds,
        MAX_BY(paper_created, received_at_ms) AS paper_created,
        MAX_BY(todo_created, received_at_ms) AS todo_created,
        MAX_BY(todo_completed, received_at_ms) AS todo_completed,
        MAX_BY(pill_expand, received_at_ms) AS pill_expand,
        MAX_BY(pill_collapse, received_at_ms) AS pill_collapse,
        MAX_BY(markdown_preview, received_at_ms) AS markdown_preview,
        MAX_BY(image_inserted, received_at_ms) AS image_inserted,
        MAX_BY(hotkey_triggered, received_at_ms) AS hotkey_triggered,
        MAX_BY(crash_count, received_at_ms) AS crash_count
    GROUP BY report_id
)
GROUP BY date
ORDER BY date
LIMIT 1000
```

## Latest completed row per report

Use this form while debugging a dashboard or a suspicious duplicate. It exposes one logical row for each deterministic completed-day report.

```sql
report_stage:complete |
SELECT
    report_id,
    MAX(received_at_ms) AS received_at_ms,
    MAX_BY(install_id, received_at_ms) AS install_id,
    MAX_BY(date, received_at_ms) AS date,
    MAX_BY(app_version, received_at_ms) AS app_version,
    MAX_BY(active_seconds, received_at_ms) AS active_seconds,
    MAX_BY(todo_created, received_at_ms) AS todo_created,
    MAX_BY(todo_completed, received_at_ms) AS todo_completed
GROUP BY report_id
LIMIT 10000
```

## Rule

Any dashboard that sums counters must follow the same two-step shape:

1. `GROUP BY report_id` and select each value with `MAX_BY(value, received_at_ms)`.
2. Aggregate those deduplicated rows by date/version/country/etc.

Raw `SUM(...)` over the ingestion rows is intentionally unsupported because retries are at-least-once.
