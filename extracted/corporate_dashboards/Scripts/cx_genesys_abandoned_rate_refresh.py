#!/usr/bin/env python3
"""
Refresh the Call Handling "Abandoned %" row from Genesys detail metrics.

Formula used for both current month and YTD:
    calls with tAbandon <= configured threshold / calls with tAnswered * 100

There is deliberately no Calls Offered - Calls Answered fallback.
Credentials are read from environment variables; no secrets are stored here.
"""

from __future__ import annotations

import argparse
import base64
import calendar
import os
import sys
import time
from dataclasses import dataclass
from datetime import date, datetime, time as dtime, timedelta, timezone
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

import pyodbc
import requests

try:
    from zoneinfo import ZoneInfo
except ImportError as exc:  # pragma: no cover - Python < 3.9
    raise RuntimeError("Python 3.9+ is required for zoneinfo.") from exc


DEFAULT_SQL_CONN = (
    "DRIVER={ODBC Driver 17 for SQL Server};"
    "SERVER=APP100;"
    "DATABASE=its_dashboard_dev;"
    "Trusted_Connection=yes;"
    "TrustServerCertificate=yes;"
)

VISUAL_KEY = "call_handling"
TARGET_TABLE = "cx.original_composition_table_row"


@dataclass(frozen=True)
class Counts:
    answered: int = 0
    abandoned_within_threshold: int = 0


@dataclass(frozen=True)
class ConversationFlags:
    started_local: datetime
    answered: bool
    abandoned_within_threshold: bool


class GenesysApiError(RuntimeError):
    def __init__(self, method: str, path: str, status_code: int, payload: Any):
        self.method = method
        self.path = path
        self.status_code = status_code
        self.payload = payload
        super().__init__(f"Genesys {method} {path} failed HTTP {status_code}: {payload}")


class GenesysClient:
    def __init__(self, environment: str, client_id: str, client_secret: str):
        self.environment = environment.strip()
        self.client_id = client_id.strip()
        self.client_secret = client_secret.strip()
        if not self.environment:
            raise ValueError("GENESYS_CLOUD_ENVIRONMENT is required.")
        if not self.client_id:
            raise ValueError("GENESYS_CLIENT_ID is required.")
        if not self.client_secret:
            raise ValueError("GENESYS_CLIENT_SECRET is required.")

        self.login_base = f"https://login.{self.environment}"
        self.api_base = f"https://api.{self.environment}"
        self._token = ""
        self._expires_at = 0.0

    def token(self) -> str:
        now = time.time()
        if self._token and now < self._expires_at - 60:
            return self._token

        basic = base64.b64encode(
            f"{self.client_id}:{self.client_secret}".encode("utf-8")
        ).decode("ascii")
        response = requests.post(
            f"{self.login_base}/oauth/token",
            headers={
                "Authorization": f"Basic {basic}",
                "Content-Type": "application/x-www-form-urlencoded",
            },
            data={"grant_type": "client_credentials"},
            timeout=60,
        )
        payload = _json_or_text(response)
        if response.status_code >= 400:
            raise GenesysApiError("POST", "/oauth/token", response.status_code, payload)

        self._token = str(payload["access_token"])
        self._expires_at = now + int(payload.get("expires_in", 3600))
        return self._token

    def post(self, path: str, body: Dict[str, Any], timeout: int = 180) -> Dict[str, Any]:
        url = f"{self.api_base}{path}"
        for attempt in range(1, 6):
            response = requests.post(
                url,
                headers={
                    "Authorization": f"Bearer {self.token()}",
                    "Content-Type": "application/json",
                },
                json=body,
                timeout=timeout,
            )
            if response.status_code in (429, 500, 502, 503, 504):
                time.sleep(min(60, attempt * 6))
                continue

            payload = _json_or_text(response)
            if response.status_code >= 400:
                raise GenesysApiError("POST", path, response.status_code, payload)
            if not isinstance(payload, dict):
                raise RuntimeError(f"Genesys returned a non-object payload for {path}.")
            return payload

        raise RuntimeError(f"Genesys POST {path} failed after retries.")


def _json_or_text(response: requests.Response) -> Any:
    try:
        return response.json()
    except Exception:
        return {"raw": response.text}


def parse_csv(value: str) -> List[str]:
    return [item.strip() for item in value.split(",") if item.strip()]


def previous_month_end(today: Optional[date] = None) -> date:
    today = today or date.today()
    return date(today.year, today.month, 1) - timedelta(days=1)


def month_start(value: date) -> date:
    return date(value.year, value.month, 1)


def month_end(value: date) -> date:
    return date(value.year, value.month, calendar.monthrange(value.year, value.month)[1])


def to_utc_interval(start_local: date, end_local_exclusive: date, tz_name: str) -> str:
    tz = ZoneInfo(tz_name)
    start = datetime.combine(start_local, dtime.min).replace(tzinfo=tz).astimezone(timezone.utc)
    end = datetime.combine(end_local_exclusive, dtime.min).replace(tzinfo=tz).astimezone(timezone.utc)
    return (
        start.isoformat(timespec="milliseconds").replace("+00:00", "Z")
        + "/"
        + end.isoformat(timespec="milliseconds").replace("+00:00", "Z")
    )


def dimension_predicate(dimension: str, value: str) -> Dict[str, Any]:
    return {
        "type": "dimension",
        "dimension": dimension,
        "operator": "matches",
        "value": value,
    }


def or_clause(dimension: str, values: Sequence[str]) -> Optional[Dict[str, Any]]:
    clean = [value for value in values if value]
    if not clean:
        return None
    return {
        "type": "or",
        "predicates": [dimension_predicate(dimension, value) for value in clean],
    }


def segment_filters(queue_ids: Sequence[str], media_type: str, direction: str) -> List[Dict[str, Any]]:
    filters: List[Optional[Dict[str, Any]]] = [
        or_clause("queueId", queue_ids),
        or_clause("mediaType", [media_type] if media_type else []),
        or_clause("direction", [direction] if direction else []),
    ]
    return [item for item in filters if item is not None]


def parse_datetime(value: Any) -> Optional[datetime]:
    if not value:
        return None
    try:
        return datetime.fromisoformat(str(value).replace("Z", "+00:00"))
    except ValueError:
        return None


def session_has_target_queue(session: Dict[str, Any], queue_ids: set[str]) -> bool:
    if not queue_ids:
        return True
    for segment in session.get("segments") or []:
        queue_id = segment.get("queueId")
        if queue_id and str(queue_id) in queue_ids:
            return True
    return False


def conversation_flags(
    conversation: Dict[str, Any],
    queue_ids: set[str],
    threshold_ms: float,
    local_tz: ZoneInfo,
) -> Optional[ConversationFlags]:
    started = parse_datetime(conversation.get("conversationStart"))
    if started is None:
        return None
    if started.tzinfo is None:
        started = started.replace(tzinfo=timezone.utc)
    started_local = started.astimezone(local_tz)

    answered = False
    abandoned_within_threshold = False

    for participant in conversation.get("participants") or []:
        for session in participant.get("sessions") or []:
            if not session_has_target_queue(session, queue_ids):
                continue

            for metric in session.get("metrics") or []:
                name = metric.get("name") or metric.get("metric")
                value = metric.get("value")
                if not isinstance(value, (int, float)):
                    continue

                if name == "tAnswered":
                    answered = True
                elif name == "tAbandon" and 0 <= float(value) <= threshold_ms:
                    abandoned_within_threshold = True

    return ConversationFlags(
        started_local=started_local,
        answered=answered,
        abandoned_within_threshold=abandoned_within_threshold,
    )


def query_flags(
    client: GenesysClient,
    interval: str,
    queue_ids: Sequence[str],
    media_type: str,
    direction: str,
    threshold_seconds: float,
    local_tz_name: str,
) -> Iterable[ConversationFlags]:
    page_size = 100
    page_number = 1
    queue_set = set(queue_ids)
    local_tz = ZoneInfo(local_tz_name)
    threshold_ms = threshold_seconds * 1000.0
    filters = segment_filters(queue_ids, media_type, direction)

    while True:
        body: Dict[str, Any] = {
            "interval": interval,
            "order": "asc",
            "orderBy": "conversationStart",
            "paging": {"pageSize": page_size, "pageNumber": page_number},
        }
        if filters:
            body["segmentFilters"] = filters

        payload = client.post("/api/v2/analytics/conversations/details/query", body)
        conversations = payload.get("conversations") or []
        for conversation in conversations:
            flags = conversation_flags(conversation, queue_set, threshold_ms, local_tz)
            if flags is not None:
                yield flags

        if len(conversations) < page_size:
            break
        page_number += 1


def summarize(flags: Iterable[ConversationFlags], current_month: date) -> Tuple[Counts, Counts]:
    current_answered = 0
    current_abandoned = 0
    ytd_answered = 0
    ytd_abandoned = 0

    for item in flags:
        if item.answered:
            ytd_answered += 1
        if item.abandoned_within_threshold:
            ytd_abandoned += 1

        if item.started_local.year == current_month.year and item.started_local.month == current_month.month:
            if item.answered:
                current_answered += 1
            if item.abandoned_within_threshold:
                current_abandoned += 1

    return (
        Counts(current_answered, current_abandoned),
        Counts(ytd_answered, ytd_abandoned),
    )


def percentage(numerator: int, denominator: int) -> float:
    if denominator <= 0:
        raise RuntimeError("Calls Answered is zero; abandoned percentage cannot be calculated.")
    return numerator / denominator * 100.0


def update_sql(
    connection_string: str,
    current_month: date,
    current: Counts,
    ytd: Counts,
    threshold_seconds: float,
) -> int:
    current_rate = percentage(current.abandoned_within_threshold, current.answered)
    ytd_rate = percentage(ytd.abandoned_within_threshold, ytd.answered)
    current_label = current_month.strftime("%B %Y")

    sql = f"""
        DECLARE @LatestSnapshot date =
        (
            SELECT MAX(snapshot_date)
            FROM {TARGET_TABLE}
            WHERE visual_key = ?
              AND ISNULL(is_sample_data, 0) = 0
        );

        IF @LatestSnapshot IS NULL
            THROW 50001, 'No current call_handling row exists in {TARGET_TABLE}.', 1;

        UPDATE {TARGET_TABLE}
        SET row_label = N'Abandoned %',
            period_label = ?,
            current_month_label = ?,
            current_month_value = ?,
            ytd_value = ?,
            status_current = CASE WHEN ? <= ISNULL(target_value, 10.0) THEN N'good' ELSE N'bad' END,
            status_ytd = CASE WHEN ? <= ISNULL(target_value, 10.0) THEN N'good' ELSE N'bad' END,
            status = CASE WHEN ? <= ISNULL(target_value, 10.0) THEN N'good' ELSE N'bad' END,
            source_name = CONCAT(N'Genesys Cloud Analytics API; abandoned<=', ?, N'sec/answered'),
            loaded_at_utc = SYSUTCDATETIME()
        WHERE visual_key = ?
          AND snapshot_date = @LatestSnapshot
          AND ISNULL(is_sample_data, 0) = 0
          AND
          (
              TRY_CONVERT(int, row_sort) = 4
              OR LOWER(COALESCE(row_label, N'')) LIKE N'%abandon%'
          );

        SELECT @@ROWCOUNT;
    """

    with pyodbc.connect(connection_string, autocommit=False) as connection:
        cursor = connection.cursor()
        row = cursor.execute(
            sql,
            VISUAL_KEY,
            current_label,
            current_label,
            current_rate,
            ytd_rate,
            current_rate,
            ytd_rate,
            current_rate,
            threshold_seconds,
            VISUAL_KEY,
        ).fetchone()
        updated = int(row[0]) if row else 0
        if updated <= 0:
            connection.rollback()
            raise RuntimeError("No abandoned call-handling row was updated.")
        connection.commit()
        return updated


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Refresh Abandoned % as tAbandon within threshold / tAnswered * 100."
    )
    parser.add_argument(
        "--snapshot-date",
        help="Completed month end in YYYY-MM-DD. Default: previous month end.",
    )
    parser.add_argument(
        "--threshold-seconds",
        type=float,
        default=float(os.environ.get("CX_ABANDON_MAX_SECONDS", "30")),
        help="Maximum tAbandon duration counted in the numerator. Default 30.",
    )
    parser.add_argument(
        "--time-zone",
        default=os.environ.get("CX_LOCAL_TIME_ZONE", "America/Toronto"),
    )
    parser.add_argument(
        "--media-type",
        default=os.environ.get("GENESYS_MEDIA_TYPE", "voice"),
    )
    parser.add_argument(
        "--direction",
        default=os.environ.get("GENESYS_DIRECTION", "inbound"),
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Calculate and print counts without updating SQL.",
    )
    return parser


def main() -> int:
    args = build_parser().parse_args()
    if args.threshold_seconds < 0:
        raise ValueError("--threshold-seconds cannot be negative.")

    snapshot = (
        month_end(datetime.strptime(args.snapshot_date, "%Y-%m-%d").date())
        if args.snapshot_date
        else previous_month_end()
    )
    current_month = month_start(snapshot)
    ytd_start = date(current_month.year, 1, 1)
    end_exclusive = date(snapshot.year, snapshot.month, 1)
    if snapshot.month == 12:
        end_exclusive = date(snapshot.year + 1, 1, 1)
    else:
        end_exclusive = date(snapshot.year, snapshot.month + 1, 1)

    environment = os.environ.get("GENESYS_CLOUD_ENVIRONMENT", "").strip()
    client_id = os.environ.get("GENESYS_CLIENT_ID", "").strip()
    client_secret = os.environ.get("GENESYS_CLIENT_SECRET", "").strip()
    queue_ids = parse_csv(os.environ.get("GENESYS_QUEUE_IDS", ""))
    if not queue_ids:
        raise ValueError("GENESYS_QUEUE_IDS is required as a comma-separated environment variable.")

    interval = to_utc_interval(ytd_start, end_exclusive, args.time_zone)
    client = GenesysClient(environment, client_id, client_secret)
    current, ytd = summarize(
        query_flags(
            client,
            interval,
            queue_ids,
            args.media_type,
            args.direction,
            args.threshold_seconds,
            args.time_zone,
        ),
        current_month,
    )

    current_rate = percentage(current.abandoned_within_threshold, current.answered)
    ytd_rate = percentage(ytd.abandoned_within_threshold, ytd.answered)

    print(f"month={current_month:%Y-%m}")
    print(f"threshold_seconds={args.threshold_seconds:g}")
    print(f"current_abandoned_within_threshold={current.abandoned_within_threshold}")
    print(f"current_answered={current.answered}")
    print(f"current_abandoned_pct={current_rate:.6f}")
    print(f"ytd_abandoned_within_threshold={ytd.abandoned_within_threshold}")
    print(f"ytd_answered={ytd.answered}")
    print(f"ytd_abandoned_pct={ytd_rate:.6f}")

    if args.dry_run:
        print("dry_run=true; SQL was not updated")
        return 0

    sql_connection = os.environ.get("CX_SQL_CONN", DEFAULT_SQL_CONN)
    updated = update_sql(
        sql_connection,
        current_month,
        current,
        ytd,
        args.threshold_seconds,
    )
    print(f"updated_rows={updated}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"FAILED: {exc}", file=sys.stderr)
        raise SystemExit(1)
