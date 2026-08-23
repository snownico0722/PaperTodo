# -*- coding: utf-8 -*-
import json
import re
from datetime import datetime, timezone


SCHEMA_VERSION = 1
ALLOWED_KINDS = {"daily_presence", "daily_usage"}
INSTALL_ID_RE = re.compile(r"^[A-Za-z0-9_-]{16,64}$")
REPORT_ID_RE = re.compile(r"^[A-Za-z0-9_-]{16,128}$")
DATE_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")


def response(status_code, body=""):
    return {
        "statusCode": status_code,
        "headers": {
            "Content-Type": "application/json; charset=utf-8"
        },
        "body": body
    }


def bounded_int(value, maximum):
    try:
        value = int(value)
    except (TypeError, ValueError):
        return 0
    return max(0, min(value, maximum))


def bounded_signed_int(value, minimum, maximum):
    try:
        value = int(value)
    except (TypeError, ValueError):
        return 0
    return max(minimum, min(value, maximum))


def clean_text(value, maximum_length):
    if not isinstance(value, str):
        return ""
    return value[:maximum_length]


def clean_date(value):
    if not isinstance(value, str) or not DATE_RE.fullmatch(value):
        return ""
    return value


def bool_as_int(value):
    # CLS key-value indexes are easier to aggregate when the wire bool is stored as 0/1.
    if value is True or value == 1 or value == "1":
        return 1
    if isinstance(value, str) and value.lower() == "true":
        return 1
    return 0


def main_handler(event, context):
    # Function URL: only accept POST. Do not log the raw request, headers, or client IP.
    if not isinstance(event, dict) or event.get("httpMethod") != "POST":
        return response(405)

    raw_body = event.get("body") or ""
    if not isinstance(raw_body, str):
        return response(400)

    # Normal reports are well below 2 KB. 8 KB leaves ample schema headroom while bounding abuse.
    if len(raw_body.encode("utf-8")) > 8192:
        return response(413)

    try:
        data = json.loads(raw_body)
    except (json.JSONDecodeError, TypeError):
        return response(400)

    if not isinstance(data, dict) or data.get("schema_version") != SCHEMA_VERSION:
        return response(400)

    kind = data.get("kind")
    if kind not in ALLOWED_KINDS:
        return response(400)

    install_id = data.get("install_id")
    report_id = data.get("report_id")
    if not isinstance(install_id, str) or not INSTALL_ID_RE.fullmatch(install_id):
        return response(400)
    if not isinstance(report_id, str) or not REPORT_ID_RE.fullmatch(report_id):
        return response(400)

    date = clean_date(data.get("date"))
    first_seen_date = clean_date(data.get("telemetry_first_seen_date"))
    if not date or not first_seen_date:
        return response(400)

    # Explicit whitelist: unknown client fields are discarded before the business record reaches CLS.
    telemetry = {
        "kind": kind,
        "schema_version": SCHEMA_VERSION,
        "report_id": report_id,
        "install_id": install_id,
        "date": date,
        "telemetry_first_seen_date": first_seen_date,

        "app_version": clean_text(data.get("app_version"), 32),
        "locale": clean_text(data.get("locale"), 16),
        "country_code": clean_text(data.get("country_code"), 8),
        "country": clean_text(data.get("country"), 64),
        "timezone_offset": bounded_signed_int(data.get("timezone_offset"), -840, 840),
        "monitor_count": bounded_int(data.get("monitor_count"), 32),

        "launch_count": bounded_int(data.get("launch_count"), 1000),
        "active_seconds": bounded_int(data.get("active_seconds"), 86400),

        "paper_count": bounded_int(data.get("paper_count"), 10000),
        "todo_paper_count": bounded_int(data.get("todo_paper_count"), 10000),
        "note_paper_count": bounded_int(data.get("note_paper_count"), 10000),
        "paper_created": bounded_int(data.get("paper_created"), 10000),
        "paper_deleted": bounded_int(data.get("paper_deleted"), 10000),

        "todo_created": bounded_int(data.get("todo_created"), 100000),
        "todo_completed": bounded_int(data.get("todo_completed"), 100000),

        "pill_enabled": bool_as_int(data.get("pill_enabled")),
        "pill_count": bounded_int(data.get("pill_count"), 10000),
        "pill_expand": bounded_int(data.get("pill_expand"), 100000),
        "pill_collapse": bounded_int(data.get("pill_collapse"), 100000),

        "markdown_preview": bounded_int(data.get("markdown_preview"), 100000),
        "image_inserted": bounded_int(data.get("image_inserted"), 10000),
        "hotkey_triggered": bounded_int(data.get("hotkey_triggered"), 100000),
        "crash_count": bounded_int(data.get("crash_count"), 1000),

        "received_at": datetime.now(timezone.utc).isoformat()
    }

    print(json.dumps(telemetry, ensure_ascii=False, separators=(",", ":")))
    return response(204)
