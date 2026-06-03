#!/usr/bin/env python3
"""Import merged events CSV into the SQLite database."""

from __future__ import annotations

import argparse
import sqlite3
import re
import unicodedata
from pathlib import Path

import pandas as pd


MASTER_DIR = Path(__file__).resolve().parent
DEFAULT_DB_PATH = MASTER_DIR / "concerts.db"
DEFAULT_CSV_PATH = MASTER_DIR / "output" / "merged_events.csv"
ARTIST_NON_ALNUM_RE = re.compile(r"[^a-z0-9]+")
LEADING_THE_RE = re.compile(r"^the\s+")


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Import merged_events.csv into canonical event tables."
    )
    parser.add_argument(
        "--db-path",
        default=str(DEFAULT_DB_PATH),
        help=f"SQLite database path (default: {DEFAULT_DB_PATH})",
    )
    parser.add_argument(
        "--csv-path",
        default=str(DEFAULT_CSV_PATH),
        help=f"Merged events CSV path (default: {DEFAULT_CSV_PATH})",
    )
    return parser


def _safe_text(value: object) -> str:
    if value is None or pd.isna(value):
        return ""
    return str(value).strip()


def _normalize_key_part(value: object) -> str:
    text = _safe_text(value)
    return text.lower()


def _canonical_key(row: pd.Series) -> str:
    parts = (
        _normalize_key_part(row.get("event_date")),
        _normalize_key_part(row.get("normalized_venue")),
        _normalize_key_part(row.get("event_start_time")),
        _normalize_key_part(row.get("normalized_title")),
    )
    return "::".join(parts)


def _split_artists(value: object) -> list[str]:
    text = _safe_text(value)
    if not text:
        return []
    artists: list[str] = []
    seen: set[str] = set()
    for piece in text.split("|"):
        artist = piece.strip()
        normalized = artist.lower()
        if artist and normalized not in seen:
            artists.append(artist)
            seen.add(normalized)
    return artists


def _normalize_artist_name(value: str) -> str:
    text = _safe_text(value)
    if not text:
        return ""
    text = unicodedata.normalize("NFKD", text)
    text = text.encode("ascii", "ignore").decode("ascii")
    text = text.lower()
    text = LEADING_THE_RE.sub("", text)
    # For artist identity matching, collapse spaces and punctuation so
    # variants like "the gathering" and "thegathering" share one key.
    text = ARTIST_NON_ALNUM_RE.sub("", text)
    return text


def _upsert_canonical_event(connection: sqlite3.Connection, row: pd.Series) -> int:
    canonical_key = _canonical_key(row)
    payload = {
        "canonical_key": canonical_key,
        "event_title": _safe_text(row.get("event_title")),
        "normalized_title": _safe_text(row.get("normalized_title")),
        "event_date": _safe_text(row.get("event_date")),
        "event_start_time": _safe_text(row.get("event_start_time")),
        "event_end_time": "",
        "venue": _safe_text(row.get("venue")),
        "normalized_venue": _safe_text(row.get("normalized_venue")),
        "city": _safe_text(row.get("city")),
        "normalized_city": _safe_text(row.get("normalized_city")),
        "timezone": "",
        "dice_url": _safe_text(row.get("dice_url")),
        "songkick_url": _safe_text(row.get("songkick_url")),
        "bandsintown_url": _safe_text(row.get("bandsintown_url")),
        "image_url": _safe_text(row.get("image_url")),
        "price": _safe_text(row.get("price")),
        "category": _safe_text(row.get("category")),
        "description": _safe_text(row.get("description")),
        "status": "active",
        "match_confidence": None,
    }

    connection.execute(
        """
        INSERT INTO canonical_events (
            canonical_key,
            event_title,
            normalized_title,
            event_date,
            event_start_time,
            event_end_time,
            venue,
            normalized_venue,
            city,
            normalized_city,
            timezone,
            dice_url,
            songkick_url,
            bandsintown_url,
            image_url,
            price,
            category,
            description,
            status,
            match_confidence
        ) VALUES (
            :canonical_key,
            :event_title,
            :normalized_title,
            :event_date,
            :event_start_time,
            :event_end_time,
            :venue,
            :normalized_venue,
            :city,
            :normalized_city,
            :timezone,
            :dice_url,
            :songkick_url,
            :bandsintown_url,
            :image_url,
            :price,
            :category,
            :description,
            :status,
            :match_confidence
        )
        ON CONFLICT(canonical_key) DO UPDATE SET
            event_title = excluded.event_title,
            normalized_title = excluded.normalized_title,
            event_date = excluded.event_date,
            event_start_time = excluded.event_start_time,
            event_end_time = excluded.event_end_time,
            venue = excluded.venue,
            normalized_venue = excluded.normalized_venue,
            city = excluded.city,
            normalized_city = excluded.normalized_city,
            timezone = excluded.timezone,
            dice_url = excluded.dice_url,
            songkick_url = excluded.songkick_url,
            bandsintown_url = excluded.bandsintown_url,
            image_url = excluded.image_url,
            price = excluded.price,
            category = excluded.category,
            description = excluded.description,
            status = excluded.status,
            match_confidence = excluded.match_confidence,
            updated_at = CURRENT_TIMESTAMP
        """,
        payload,
    )

    row_id = connection.execute(
        "SELECT id FROM canonical_events WHERE canonical_key = ?",
        (canonical_key,),
    ).fetchone()
    if row_id is None:
        raise RuntimeError(f"Failed to load canonical event for key {canonical_key}")
    return int(row_id[0])


def _upsert_artist(connection: sqlite3.Connection, artist_name: str) -> int:
    normalized_name = _normalize_artist_name(artist_name)
    connection.execute(
        """
        INSERT INTO artists (
            name,
            formal_name,
            normalized_name,
            artist_tags,
            sociallinks
        ) VALUES (?, ?, ?, ?, ?)
        ON CONFLICT(normalized_name) DO UPDATE SET
            name = excluded.name,
            formal_name = excluded.formal_name,
            updated_at = CURRENT_TIMESTAMP
        """,
        (artist_name, "", normalized_name, "[]", "[]"),
    )
    row = connection.execute(
        "SELECT id FROM artists WHERE normalized_name = ?",
        (normalized_name,),
    ).fetchone()
    if row is None:
        raise RuntimeError(f"Failed to load artist {artist_name}")
    return int(row[0])


def _replace_event_artists(
    connection: sqlite3.Connection, canonical_event_id: int, artists: list[str]
) -> int:
    connection.execute(
        "DELETE FROM event_artists WHERE canonical_event_id = ?",
        (canonical_event_id,),
    )

    linked = 0
    for billing_position, artist_name in enumerate(artists, start=1):
        artist_id = _upsert_artist(connection, artist_name)
        connection.execute(
            """
            INSERT INTO event_artists (
                canonical_event_id,
                artist_id,
                billing_position,
                role
            ) VALUES (?, ?, ?, ?)
            """,
            (canonical_event_id, artist_id, billing_position, "performer"),
        )
        linked += 1
    return linked


def main() -> int:
    parser = _build_parser()
    args = parser.parse_args()

    db_path = Path(args.db_path)
    csv_path = Path(args.csv_path)

    if not db_path.exists():
        parser.error(f"Database not found: {db_path}")
    if not csv_path.exists():
        parser.error(f"CSV not found: {csv_path}")

    df = pd.read_csv(csv_path).fillna("")
    imported_events = 0
    linked_artists = 0

    with sqlite3.connect(db_path) as connection:
        connection.execute("PRAGMA foreign_keys = ON")
        for _, row in df.iterrows():
            canonical_event_id = _upsert_canonical_event(connection, row)
            artists = _split_artists(row.get("artists"))
            linked_artists += _replace_event_artists(connection, canonical_event_id, artists)
            imported_events += 1
        connection.commit()

        canonical_count = connection.execute(
            "SELECT COUNT(*) FROM canonical_events"
        ).fetchone()[0]
        artist_count = connection.execute(
            "SELECT COUNT(*) FROM artists"
        ).fetchone()[0]
        event_artist_count = connection.execute(
            "SELECT COUNT(*) FROM event_artists"
        ).fetchone()[0]

    print(f"Imported merged rows: {imported_events}")
    print(f"Artist links refreshed: {linked_artists}")
    print(f"canonical_events rows: {canonical_count}")
    print(f"artists rows: {artist_count}")
    print(f"event_artists rows: {event_artist_count}")
    print(f"Database: {db_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
