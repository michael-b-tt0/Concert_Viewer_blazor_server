#!/usr/bin/env python3
"""Initialize the SQLite database for concert data."""

from __future__ import annotations

import argparse
import sqlite3
from pathlib import Path


MASTER_DIR = Path(__file__).resolve().parent
DEFAULT_DB_PATH = MASTER_DIR / "concerts.db"
DEFAULT_SCHEMA_PATH = MASTER_DIR / "schema.sql"


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Create or update the local SQLite database schema."
    )
    parser.add_argument(
        "--db-path",
        default=str(DEFAULT_DB_PATH),
        help=f"SQLite database path (default: {DEFAULT_DB_PATH})",
    )
    parser.add_argument(
        "--schema-path",
        default=str(DEFAULT_SCHEMA_PATH),
        help=f"Schema SQL path (default: {DEFAULT_SCHEMA_PATH})",
    )
    return parser


def _list_tables(connection: sqlite3.Connection) -> list[str]:
    rows = connection.execute(
        """
        SELECT name
        FROM sqlite_master
        WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
        ORDER BY name
        """
    ).fetchall()
    return [row[0] for row in rows]


def main() -> int:
    parser = _build_parser()
    args = parser.parse_args()

    db_path = Path(args.db_path)
    schema_path = Path(args.schema_path)

    if not schema_path.exists():
        parser.error(f"Schema file not found: {schema_path}")

    db_path.parent.mkdir(parents=True, exist_ok=True)
    schema_sql = schema_path.read_text(encoding="utf-8")

    with sqlite3.connect(db_path) as connection:
        connection.execute("PRAGMA foreign_keys = ON")
        connection.executescript(schema_sql)
        connection.commit()
        tables = _list_tables(connection)

    print(f"Initialized database: {db_path}")
    print(f"Applied schema: {schema_path}")
    print(f"Tables ({len(tables)}): {', '.join(tables)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
