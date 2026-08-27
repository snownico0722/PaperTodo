# CLS telemetry queries

PaperTodo telemetry ingestion is append-only. A network retry can therefore leave more than one raw CLS row with the same deterministic `report_id`. The first-day provisional row and that day's later completed row deliberately use the same `report_id` as well.

Do not aggregate raw rows directly. Deduplicate by `report_id` first, keeping values from the row with the greatest `received_at_ms` via `max_by(value, received_at_ms)`, then aggregate the deduplicated result.

These examples use Tencent CLS nested subqueries, `group by`, `count(distinct ...)`, and `max_by(x, y)`.

## Required indexes

Enable key-value indexes and statistics for fields used by the queries, especially:

- `report_id`
- `install_id`
- `date`
- `telemetry_first_seen_date`
- `received_at_ms`
- `crash_exception_type`
- `crash_stack_hash`
- `crash_module`
- every numeric metric that will be aggregated

## New telemetry users per day

A brand-new telemetry install has `date = telemetry_first_seen_date`. Deduplicate first so a later completed row replaces the provisional first-day row instead of counting the same install twice.

```sql
* |
SELECT date, COUNT(*) AS new_users
FROM (
    SELECT
        report_id,
        MAX_BY(date, received_at_ms) AS date,
        MAX_BY(telemetry_first_seen_date, received_at_ms) AS telemetry_first_seen_date
    GROUP BY report_id
)
WHERE date = telemetry_first_seen_date
GROUP BY date
ORDER BY date
LIMIT 1000
```

## Daily active installs

There is one logical daily `report_id` per install/date. Count distinct installs after retry/provisional deduplication.

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

For very large datasets, Tencent CLS recommends `approx_distinct(install_id)` when exact `count(distinct ...)` uses too much analysis memory.

## Daily usage totals

This returns the best available row for every install/day. For users who return after their first day, the completed row replaces the provisional row automatically. A one-time user who never launches again contributes only the small provisional snapshot that was actually observed.

```sql
* |
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

## Crash families

A daily report stores the total `crash_count` for that install/day but only the most recently observed anonymous crash signature. Do not sum `crash_count` by signature: doing so would incorrectly attribute earlier crashes on the same day to the last signature. Use the daily usage query above for total crash volume; use this query to rank signatures by affected install-days and installs.

```sql
* |
SELECT
    app_version,
    crash_exception_type,
    crash_stack_hash,
    crash_module,
    COUNT(*) AS affected_install_days,
    COUNT(DISTINCT install_id) AS affected_installs
FROM (
    SELECT
        report_id,
        MAX_BY(install_id, received_at_ms) AS install_id,
        MAX_BY(app_version, received_at_ms) AS app_version,
        MAX_BY(crash_count, received_at_ms) AS crash_count,
        MAX_BY(crash_exception_type, received_at_ms) AS crash_exception_type,
        MAX_BY(crash_stack_hash, received_at_ms) AS crash_stack_hash,
        MAX_BY(crash_module, received_at_ms) AS crash_module
    GROUP BY report_id
)
WHERE crash_count > 0 AND crash_stack_hash <> ''
GROUP BY app_version, crash_exception_type, crash_stack_hash, crash_module
ORDER BY affected_installs DESC, affected_install_days DESC
LIMIT 1000
```

## Latest logical row per report

Use this while debugging a dashboard or suspicious retry. It exposes one logical row for each deterministic install/day report.

```sql
* |
SELECT
    report_id,
    MAX(received_at_ms) AS received_at_ms,
    MAX_BY(install_id, received_at_ms) AS install_id,
    MAX_BY(date, received_at_ms) AS date,
    MAX_BY(telemetry_first_seen_date, received_at_ms) AS telemetry_first_seen_date,
    MAX_BY(app_version, received_at_ms) AS app_version,
    MAX_BY(active_seconds, received_at_ms) AS active_seconds,
    MAX_BY(todo_created, received_at_ms) AS todo_created,
    MAX_BY(todo_completed, received_at_ms) AS todo_completed,
    MAX_BY(crash_count, received_at_ms) AS crash_count,
    MAX_BY(crash_exception_type, received_at_ms) AS crash_exception_type,
    MAX_BY(crash_stack_hash, received_at_ms) AS crash_stack_hash,
    MAX_BY(crash_module, received_at_ms) AS crash_module
GROUP BY report_id
LIMIT 10000
```

## Rule

Any dashboard that sums counters or counts installs must follow the same two-step shape:

1. `GROUP BY report_id` and select each value with `MAX_BY(value, received_at_ms)`.
2. Aggregate those deduplicated rows by date/version/country/etc.

Raw `SUM(...)` or raw row counts over ingestion rows are intentionally unsupported because delivery is at-least-once and the first-day provisional row is later superseded by the completed row.
