#!/usr/bin/env python3
"""Normalize scraper outputs and combine likely duplicate concert events."""

from __future__ import annotations

import argparse
import json
import math
import re
import unicodedata
from collections import defaultdict
from dataclasses import dataclass
from datetime import datetime
from difflib import SequenceMatcher
from pathlib import Path

import pandas as pd


MASTER_DIR = Path(__file__).resolve().parent
OUTPUT_DIR = MASTER_DIR / "output"

DEFAULT_INPUTS = {
    "bandsintown": OUTPUT_DIR / "bandsintown_events.csv",
    "dice": OUTPUT_DIR / "dice_events.csv",
    "songkick": OUTPUT_DIR / "songkick_events.csv",
}
DEFAULT_VENUE_BLACKLIST_PATH = MASTER_DIR / "venue_blacklist.txt"
DEFAULT_CITY_BLACKLIST_PATH = MASTER_DIR / "city_blacklist.txt"
DEFAULT_VENUE_ALIASES_PATH = MASTER_DIR / "venue_aliases.txt"

BOROUGH_TO_LONDON = {
    "archway": "london",
    "brixton": "london",
    "camden": "london",
    "clapham": "london",
    "dalston": "london",
    "greenwich": "london",
    "hackney": "london",
    "highgate": "london",
    "islington": "london",
    "kentish town": "london",
    "peckham": "london",
    "shoreditch": "london",
    "soho": "london",
    "walthamstow": "london",
}

TITLE_DELIMITERS = re.compile(r"\s*(?:\||,| with | and | presents:| presents | ft\. | feat\. )\s*")
PUNCTUATION_RE = re.compile(r"[^\w\s]")
APOSTROPHE_RE = re.compile(r"[\'’]")
SPACE_RE = re.compile(r"\s+")
ARTIST_IDENTITY_RE = re.compile(r"[^a-z0-9]+")
LEADING_THE_RE = re.compile(r"^the\s+")


@dataclass(frozen=True)
class MatchResult:
    """Pairwise match decision."""

    score: int
    reasons: tuple[str, ...]


class DisjointSet:
    """Union-find structure for clustering matches."""

    def __init__(self, items: list[str]) -> None:
        self.parent = {item: item for item in items}

    def find(self, item: str) -> str:
        root = item
        while self.parent[root] != root:
            root = self.parent[root]
        while self.parent[item] != item:
            parent = self.parent[item]
            self.parent[item] = root
            item = parent
        return root

    def union(self, left: str, right: str) -> None:
        left_root = self.find(left)
        right_root = self.find(right)
        if left_root != right_root:
            self.parent[right_root] = left_root


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=(
            "Normalize the three scraper outputs and combine likely duplicate events."
        )
    )
    parser.add_argument(
        "--bandsintown",
        default=str(DEFAULT_INPUTS["bandsintown"]),
        help="Path to the Bandsintown CSV",
    )
    parser.add_argument(
        "--dice",
        default=str(DEFAULT_INPUTS["dice"]),
        help="Path to the DICE CSV",
    )
    parser.add_argument(
        "--songkick",
        default=str(DEFAULT_INPUTS["songkick"]),
        help="Path to the Songkick CSV",
    )
    parser.add_argument(
        "--output-dir",
        default=str(OUTPUT_DIR),
        help="Directory for normalized and merged outputs",
    )
    parser.add_argument(
        "--match-threshold",
        type=int,
        default=60,
        help="Minimum score required to auto-merge two events",
    )
    parser.add_argument(
        "--review-threshold",
        type=int,
        default=40,
        help="Minimum score required to include a pair in review_candidates.csv",
    )
    parser.add_argument(
        "--interactive-review",
        action="store_true",
        default=True,
        help="Prompt in the CLI to manually accept or reject review candidates",
    )
    parser.add_argument(
        "--venue-blacklist",
        default=str(DEFAULT_VENUE_BLACKLIST_PATH),
        help=(
            "Optional text file with one venue per line to exclude before matching "
            f"(default: {DEFAULT_VENUE_BLACKLIST_PATH})"
        ),
    )
    parser.add_argument(
        "--city-blacklist",
        default=str(DEFAULT_CITY_BLACKLIST_PATH),
        help=(
            "Optional text file with one city per line to exclude before matching "
            f"(default: {DEFAULT_CITY_BLACKLIST_PATH})"
        ),
    )
    parser.add_argument(
        "--venue-aliases",
        default=str(DEFAULT_VENUE_ALIASES_PATH),
        help=(
            "Optional text file with `alias => canonical venue` mappings applied "
            "before matching "
            f"(default: {DEFAULT_VENUE_ALIASES_PATH})"
        ),
    )
    parser.add_argument(
        "--event-cutoff-time",
        default="",
        help=(
            "Optional HH:MM cutoff; events starting before this time are excluded "
            "before matching"
        ),
    )
    return parser


def _safe_text(value: object) -> str:
    if value is None or (isinstance(value, float) and math.isnan(value)):
        return ""
    return str(value).strip()


def _normalize_text(value: object) -> str:
    text = _safe_text(value)
    if not text:
        return ""
    text = unicodedata.normalize("NFKD", text)
    text = text.encode("ascii", "ignore").decode("ascii")
    text = text.lower()
    text = text.replace("&", " and ")
    # Drop apostrophes without adding spaces so "Luke's" becomes "lukes".
    text = APOSTROPHE_RE.sub("", text)
    text = PUNCTUATION_RE.sub(" ", text)
    text = SPACE_RE.sub(" ", text).strip()
    return text


def _normalize_city(value: object) -> str:
    city = _normalize_text(value)
    return BOROUGH_TO_LONDON.get(city, city)


def _extract_city_from_location(value: object) -> str:
    text = _safe_text(value)
    if not text:
        return ""
    return text.split(",", 1)[0].strip()


def _parse_date_only(value: object) -> str:
    text = _safe_text(value)
    if not text:
        return ""
    parsed = pd.to_datetime(text, errors="coerce", utc=False)
    if pd.isna(parsed):
        return ""
    return parsed.strftime("%Y-%m-%d")


def _parse_time_only(value: object) -> str:
    text = _safe_text(value)
    if not text:
        return ""

    parsed = pd.to_datetime(text, errors="coerce", utc=False)
    if not pd.isna(parsed):
        return parsed.strftime("%H:%M")

    for fmt in ("%H:%M", "%H:%M:%S"):
        try:
            return datetime.strptime(text, fmt).strftime("%H:%M")
        except ValueError:
            continue
    return ""


def _split_artists(value: object) -> list[str]:
    text = _safe_text(value)
    if not text:
        return []
    parts = TITLE_DELIMITERS.split(text)
    normalized = []
    seen = set()
    for part in parts:
        clean = _normalize_text(part)
        identity = _artist_identity_key(clean)
        if clean and identity and identity not in seen:
            normalized.append(clean)
            seen.add(identity)
    return normalized


def _artist_identity_key(value: object) -> str:
    text = _normalize_text(value)
    if not text:
        return ""
    text = LEADING_THE_RE.sub("", text)
    return ARTIST_IDENTITY_RE.sub("", text)


def _choose_title(row: pd.Series, source: str) -> str:
    if source == "bandsintown":
        return _safe_text(row.get("event_title")) or _safe_text(row.get("artist_name"))
    return _safe_text(row.get("event_title"))


def _title_similarity(left: str, right: str) -> float:
    if not left or not right:
        return 0.0
    if left in right or right in left:
        return 1.0
    return SequenceMatcher(None, left, right).ratio()


def _venue_similarity(left: str, right: str) -> float:
    if not left or not right:
        return 0.0
    if left == right:
        return 1.0
    if left in right or right in left:
        return 0.92
    return SequenceMatcher(None, left, right).ratio()


def _time_difference_minutes(left: str, right: str) -> int | None:
    if not left or not right:
        return None
    left_time = datetime.strptime(left, "%H:%M")
    right_time = datetime.strptime(right, "%H:%M")
    return abs(int((left_time - right_time).total_seconds() // 60))


def _time_to_minutes(value: str) -> int | None:
    if not value:
        return None
    parsed = datetime.strptime(value, "%H:%M")
    return parsed.hour * 60 + parsed.minute


def _normalize_cutoff_time(value: object) -> str:
    text = _safe_text(value)
    if not text:
        return ""
    normalized = _parse_time_only(text)
    if not normalized:
        raise ValueError("Invalid --event-cutoff-time value; expected HH:MM")
    return normalized


def _load_venue_blacklist(path: Path) -> set[str]:
    if not path.exists():
        return set()

    blocked: set[str] = set()
    for raw_line in path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        normalized = _normalize_text(line)
        if normalized:
            blocked.add(normalized)
    return blocked


def _load_venue_aliases(path: Path) -> dict[str, str]:
    if not path.exists():
        return {}

    aliases: dict[str, str] = {}
    for line_number, raw_line in enumerate(
        path.read_text(encoding="utf-8").splitlines(),
        start=1,
    ):
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        if "=>" not in line:
            raise ValueError(
                f"Invalid venue alias mapping on line {line_number} of {path}: "
                "expected `alias => canonical venue`"
            )
        alias_text, canonical_text = (part.strip() for part in line.split("=>", 1))
        normalized_canonical = _normalize_text(canonical_text)
        alias_parts = [_normalize_text(part) for part in alias_text.split("|")]
        normalized_aliases = [alias for alias in alias_parts if alias]
        if not normalized_aliases or not normalized_canonical:
            raise ValueError(
                f"Invalid venue alias mapping on line {line_number} of {path}: "
                "alias and canonical venue must both be non-empty"
            )
        for normalized_alias in normalized_aliases:
            aliases[normalized_alias] = normalized_canonical
    return aliases


def _apply_venue_alias(value: object, venue_aliases: dict[str, str]) -> str:
    normalized_venue = _normalize_text(value)
    if not normalized_venue:
        return ""
    return venue_aliases.get(normalized_venue, normalized_venue)


def _apply_venue_blacklist(
    frames: list[pd.DataFrame], blocked_venues: set[str]
) -> tuple[list[pd.DataFrame], pd.DataFrame]:
    if not blocked_venues:
        return frames, pd.DataFrame()

    kept_frames: list[pd.DataFrame] = []
    excluded_frames: list[pd.DataFrame] = []

    for frame in frames:
        if frame.empty:
            kept_frames.append(frame)
            continue

        mask = frame["normalized_venue"].isin(blocked_venues)
        kept_frames.append(frame.loc[~mask].copy())

        excluded = frame.loc[mask].copy()
        if not excluded.empty:
            excluded["exclusion_reason"] = "venue_blacklist"
            excluded_frames.append(excluded)

    if not excluded_frames:
        return kept_frames, pd.DataFrame()

    excluded_df = pd.concat(excluded_frames, ignore_index=True)
    excluded_df = excluded_df.sort_values(
        by=["event_date", "normalized_venue", "event_start_time", "source"],
        kind="stable",
    ).reset_index(drop=True)
    return kept_frames, excluded_df


def _apply_city_blacklist(
    frames: list[pd.DataFrame], blocked_cities: set[str]
) -> tuple[list[pd.DataFrame], pd.DataFrame]:
    if not blocked_cities:
        return frames, pd.DataFrame()

    kept_frames: list[pd.DataFrame] = []
    excluded_frames: list[pd.DataFrame] = []

    for frame in frames:
        if frame.empty:
            kept_frames.append(frame)
            continue

        mask = frame["normalized_city"].isin(blocked_cities)
        kept_frames.append(frame.loc[~mask].copy())

        excluded = frame.loc[mask].copy()
        if not excluded.empty:
            excluded["exclusion_reason"] = "city_blacklist"
            excluded_frames.append(excluded)

    if not excluded_frames:
        return kept_frames, pd.DataFrame()

    excluded_df = pd.concat(excluded_frames, ignore_index=True)
    excluded_df = excluded_df.sort_values(
        by=["event_date", "normalized_city", "normalized_venue", "event_start_time", "source"],
        kind="stable",
    ).reset_index(drop=True)
    return kept_frames, excluded_df


def _apply_event_cutoff(
    frames: list[pd.DataFrame], cutoff_time: str
) -> tuple[list[pd.DataFrame], pd.DataFrame]:
    if not cutoff_time:
        return frames, pd.DataFrame()

    cutoff_minutes = _time_to_minutes(cutoff_time)
    if cutoff_minutes is None:
        raise ValueError("Invalid event cutoff time")

    kept_frames: list[pd.DataFrame] = []
    excluded_frames: list[pd.DataFrame] = []

    for frame in frames:
        if frame.empty:
            kept_frames.append(frame)
            continue

        start_minutes = frame["event_start_time"].apply(
            lambda value: _time_to_minutes(_safe_text(value))
        )
        mask = start_minutes.apply(
            lambda value: value is not None and value < cutoff_minutes
        )
        kept_frames.append(frame.loc[~mask].copy())

        excluded = frame.loc[mask].copy()
        if not excluded.empty:
            excluded["exclusion_reason"] = f"event_cutoff_before_{cutoff_time}"
            excluded_frames.append(excluded)

    if not excluded_frames:
        return kept_frames, pd.DataFrame()

    excluded_df = pd.concat(excluded_frames, ignore_index=True)
    excluded_df = excluded_df.sort_values(
        by=["event_date", "normalized_city", "normalized_venue", "event_start_time", "source"],
        kind="stable",
    ).reset_index(drop=True)
    return kept_frames, excluded_df


def _combine_excluded_frames(*frames: pd.DataFrame) -> pd.DataFrame:
    available_frames = [frame for frame in frames if not frame.empty]
    if not available_frames:
        return pd.DataFrame()
    combined = pd.concat(available_frames, ignore_index=True)
    return combined.sort_values(
        by=["event_date", "normalized_city", "normalized_venue", "event_start_time", "source"],
        kind="stable",
    ).reset_index(drop=True)


def _prepare_bandsintown(path: Path, venue_aliases: dict[str, str]) -> pd.DataFrame:
    df = pd.read_csv(path).fillna("")
    rows = []
    for _, row in df.iterrows():
        raw_city = _extract_city_from_location(row.get("location_text"))
        artists = _split_artists(row.get("artist_name"))
        title = _choose_title(row, "bandsintown")
        rows.append(
            {
                "source": "bandsintown",
                "source_event_id": _safe_text(row.get("event_id")),
                "event_title": title,
                "event_date": _parse_date_only(row.get("date")),
                "event_start_time": _parse_time_only(row.get("start_time")),
                "event_end_time": _parse_time_only(row.get("end_time")),
                "venue": _safe_text(row.get("venue_name")),
                "raw_city": raw_city,
                "location_text": _safe_text(row.get("location_text")),
                "timezone": _safe_text(row.get("timezone")),
                "event_url": _safe_text(row.get("event_url")),
                "artist_url": _safe_text(row.get("artist_url")),
                "image_url": _safe_text(row.get("image_url")),
                "price": "",
                "category": "",
                "description": "",
                "call_to_action": _safe_text(row.get("call_to_action")),
                "rsvp_count": _safe_text(row.get("rsvp_count")),
                "scraped_at": _safe_text(row.get("scraped_at")),
                "raw_artists": _safe_text(row.get("artist_name")),
                "normalized_title": _normalize_text(title),
                "normalized_venue": _apply_venue_alias(row.get("venue_name"), venue_aliases),
                "normalized_city": _normalize_city(raw_city),
                "normalized_artists": artists,
            }
        )

    normalized = pd.DataFrame(rows)
    if normalized.empty:
        return normalized

    aggregated_rows = []
    base_group_columns = ["event_date", "normalized_venue"]
    for _, venue_group in normalized.groupby(base_group_columns, dropna=False, sort=False):
        group_records = venue_group.to_dict("records")
        clusters: list[list[dict[str, object]]] = []

        for record in group_records:
            record_time = _time_to_minutes(_safe_text(record.get("event_start_time")))
            placed = False

            for cluster in clusters:
                cluster_times = [
                    _time_to_minutes(_safe_text(cluster_record.get("event_start_time")))
                    for cluster_record in cluster
                ]
                cluster_times = [value for value in cluster_times if value is not None]

                if record_time is None or not cluster_times:
                    cluster.append(record)
                    placed = True
                    break

                if min(abs(record_time - cluster_time) for cluster_time in cluster_times) <= 120:
                    cluster.append(record)
                    placed = True
                    break

            if not placed:
                clusters.append([record])

        for group_records_cluster in clusters:
            group = pd.DataFrame(group_records_cluster)
            artists = sorted(
                {artist for artists in group["normalized_artists"] for artist in artists}
            )
            source_ids = sorted(set(filter(None, group["source_event_id"])))
            titles = [value for value in group["event_title"] if _safe_text(value)]
            image_urls = [value for value in group["image_url"] if _safe_text(value)]
            event_urls = [value for value in group["event_url"] if _safe_text(value)]
            sorted_start_times = sorted(
                [value for value in group["event_start_time"] if _safe_text(value)]
            )
            representative_start_time = sorted_start_times[0] if sorted_start_times else ""
            aggregated_rows.append(
                {
                    "source": "bandsintown",
                    "source_row_id": (
                        f"bandsintown::{group.iloc[0]['event_date']}::"
                        f"{group.iloc[0]['normalized_venue']}::"
                        f"{representative_start_time or 'unknown'}"
                    ),
                    "source_event_id": "|".join(source_ids),
                    "event_title": next((title for title in titles if title), ""),
                    "event_date": group.iloc[0]["event_date"],
                    "event_start_time": representative_start_time,
                    "event_end_time": next((value for value in group["event_end_time"] if value), ""),
                    "venue": next((value for value in group["venue"] if value), ""),
                    "raw_city": next((value for value in group["raw_city"] if value), ""),
                    "location_text": next((value for value in group["location_text"] if value), ""),
                    "timezone": next((value for value in group["timezone"] if value), ""),
                    "event_url": next((value for value in event_urls if value), ""),
                    "artist_url": next((value for value in group["artist_url"] if value), ""),
                    "image_url": next((value for value in image_urls if value), ""),
                    "price": "",
                    "category": "",
                    "description": "",
                    "call_to_action": next((value for value in group["call_to_action"] if value), ""),
                    "rsvp_count": str(max((int(value) for value in group["rsvp_count"] if _safe_text(value)), default=0)),
                    "scraped_at": max(group["scraped_at"]) if len(group["scraped_at"]) else "",
                    "raw_artists": " | ".join(dict.fromkeys(filter(None, group["raw_artists"]))),
                    "normalized_title": _normalize_text(next((title for title in titles if title), "")),
                    "normalized_venue": group.iloc[0]["normalized_venue"],
                    "normalized_city": group.iloc[0]["normalized_city"],
                    "normalized_artists": artists,
                }
            )
    return pd.DataFrame(aggregated_rows)


def _prepare_dice(path: Path, venue_aliases: dict[str, str]) -> pd.DataFrame:
    df = pd.read_csv(path).fillna("")
    rows = []
    for _, row in df.iterrows():
        title = _choose_title(row, "dice")
        raw_city = _safe_text(row.get("city"))
        artists = _split_artists(row.get("artists")) or _split_artists(title)
        source_event_id = _safe_text(row.get("event_id"))
        rows.append(
            {
                "source": "dice",
                "source_row_id": f"dice::{source_event_id}",
                "source_event_id": source_event_id,
                "event_title": title,
                "event_date": _parse_date_only(row.get("date")),
                "event_start_time": _parse_time_only(row.get("start_time") or row.get("date")),
                "event_end_time": "",
                "venue": _safe_text(row.get("venue")),
                "raw_city": raw_city,
                "location_text": raw_city,
                "timezone": "",
                "event_url": _safe_text(row.get("event_url")),
                "artist_url": "",
                "image_url": _safe_text(row.get("image_url")),
                "price": _safe_text(row.get("price")),
                "category": _safe_text(row.get("category")),
                "description": _safe_text(row.get("about_text")),
                "call_to_action": "",
                "rsvp_count": "",
                "scraped_at": _safe_text(row.get("scraped_at")),
                "raw_artists": _safe_text(row.get("artists")),
                "normalized_title": _normalize_text(title),
                "normalized_venue": _apply_venue_alias(row.get("venue"), venue_aliases),
                "normalized_city": _normalize_city(raw_city),
                "normalized_artists": artists,
            }
        )
    return pd.DataFrame(rows)


def _prepare_songkick(path: Path, venue_aliases: dict[str, str]) -> pd.DataFrame:
    df = pd.read_csv(path).fillna("")
    rows = []
    for _, row in df.iterrows():
        title = _choose_title(row, "songkick")
        raw_city = _safe_text(row.get("city"))
        source_event_id = _safe_text(row.get("event_id"))
        rows.append(
            {
                "source": "songkick",
                "source_row_id": f"songkick::{source_event_id}",
                "source_event_id": source_event_id,
                "event_title": title,
                "event_date": _parse_date_only(row.get("date")),
                "event_start_time": _parse_time_only(row.get("start_time")),
                "event_end_time": "",
                "venue": _safe_text(row.get("venue")),
                "raw_city": raw_city,
                "location_text": raw_city,
                "timezone": "",
                "event_url": _safe_text(row.get("event_url")),
                "artist_url": "",
                "image_url": _safe_text(row.get("image_url")),
                "price": "",
                "category": "",
                "description": "",
                "call_to_action": "",
                "rsvp_count": "",
                "scraped_at": _safe_text(row.get("scraped_at")),
                "raw_artists": _safe_text(row.get("artists")),
                "normalized_title": _normalize_text(title),
                "normalized_venue": _apply_venue_alias(row.get("venue"), venue_aliases),
                "normalized_city": _normalize_city(raw_city),
                "normalized_artists": _split_artists(row.get("artists")),
            }
        )
    return pd.DataFrame(rows)


def _rows_match(left: pd.Series, right: pd.Series) -> MatchResult:
    score = 0
    reasons: list[str] = []

    venue_similarity = _venue_similarity(
        left["normalized_venue"], right["normalized_venue"]
    )
    if venue_similarity >= 0.99:
        score += 35
        reasons.append("exact_venue")
    elif venue_similarity >= 0.90:
        score += 25
        reasons.append("similar_venue")

    shared_artists = sorted(
        set(left["normalized_artists"]).intersection(right["normalized_artists"])
    )
    if shared_artists:
        score += 35
        reasons.append(f"shared_artists:{'|'.join(shared_artists[:3])}")

    title_similarity = _title_similarity(
        left["normalized_title"], right["normalized_title"]
    )
    if title_similarity >= 0.95:
        score += 25
        reasons.append("exactish_title")
    elif title_similarity >= 0.75:
        score += 15
        reasons.append("similar_title")

    time_diff = _time_difference_minutes(
        left["event_start_time"], right["event_start_time"]
    )
    if time_diff is not None:
        if time_diff <= 30:
            score += 15
            reasons.append("time_within_30m")
        elif time_diff <= 90:
            score += 8
            reasons.append("time_within_90m")

    if (
        left["normalized_city"]
        and right["normalized_city"]
        and left["normalized_city"] == right["normalized_city"]
    ):
        score += 10
        reasons.append("same_city")

    return MatchResult(score=score, reasons=tuple(reasons))


def _build_matches(
    normalized_df: pd.DataFrame, match_threshold: int, review_threshold: int
) -> tuple[list[tuple[str, str, MatchResult]], list[dict[str, object]]]:
    approved_matches: list[tuple[str, str, MatchResult]] = []
    review_rows: list[dict[str, object]] = []

    for event_date, group in normalized_df.groupby("event_date", sort=False):
        if not event_date:
            continue
        rows = list(group.to_dict("records"))
        for index, left in enumerate(rows):
            for right in rows[index + 1 :]:
                if left["source"] == right["source"]:
                    continue
                result = _rows_match(pd.Series(left), pd.Series(right))
                if result.score >= match_threshold:
                    approved_matches.append((left["source_row_id"], right["source_row_id"], result))
                elif result.score >= review_threshold:
                    review_rows.append(
                        {
                            "left_source_row_id": left["source_row_id"],
                            "right_source_row_id": right["source_row_id"],
                            "event_date": event_date,
                            "left_source": left["source"],
                            "left_title": left["event_title"],
                            "left_venue": left["venue"],
                            "left_time": left["event_start_time"],
                            "left_artists": " | ".join(left["normalized_artists"]),
                            "right_source": right["source"],
                            "right_title": right["event_title"],
                            "right_venue": right["venue"],
                            "right_time": right["event_start_time"],
                            "right_artists": " | ".join(right["normalized_artists"]),
                            "score": result.score,
                            "reasons": " | ".join(result.reasons),
                            "decision": "pending",
                            
                        }
                    )
    return approved_matches, review_rows
    


def _interactive_review(
    review_rows: list[dict[str, object]],
) -> list[tuple[str, str, MatchResult]]:
    approved_from_review: list[tuple[str, str, MatchResult]] = []
    if not review_rows:
        print("No review candidates to inspect.")
        return approved_from_review

    print("\nInteractive review")
    print("Reply with: y = merge, n = keep separate, q = quit review")

    for index, row in enumerate(review_rows, start=1):
        print(f"\nCandidate {index}/{len(review_rows)}")
        print(
            f"Date: {row['event_date']} | Score: {row['score']} | Reasons: {row['reasons']}"
        )
        print(
            f"Left : [{row['left_source']}] {row['left_title']} | {row['left_venue']} | "
            f"{row['left_time'] or 'unknown time'} | artists: {row['left_artists'] or 'n/a'}"
        )
        print(
            f"Right: [{row['right_source']}] {row['right_title']} | {row['right_venue']} | "
            f"{row['right_time'] or 'unknown time'} | artists: {row['right_artists'] or 'n/a'}"
        )

        while True:
            response = input("Merge these records? [y/n/q]: ").strip().lower()
            if response in {"y", "n", "q"}:
                break
            print("Please enter y, n, or q.")

        if response == "q":
            row["decision"] = "quit"
            print("Stopping interactive review. Remaining candidates stay pending.")
            break
        if response == "y":
            row["decision"] = "accepted"
            approved_from_review.append(
                (
                    str(row["left_source_row_id"]),
                    str(row["right_source_row_id"]),
                    MatchResult(
                        score=int(row["score"]),
                        reasons=(f"manual_review:{row['reasons']}",),
                    ),
                )
            )
        elif response == "n":
            row["decision"] = "rejected"

    return approved_from_review


def _coalesce(values: list[str]) -> str:
    for value in values:
        if _safe_text(value):
            return _safe_text(value)
    return ""


def _merge_cluster(cluster_df: pd.DataFrame, cluster_id: int) -> dict[str, str]:
    artist_map: dict[str, str] = {}
    for artist_list in cluster_df["normalized_artists"]:
        for artist in artist_list:
            identity = _artist_identity_key(artist)
            if identity and identity not in artist_map:
                artist_map[identity] = artist
    artists = sorted(artist_map.values())
    source_to_row = {
        source: cluster_df[cluster_df["source"] == source].iloc[0]
        for source in cluster_df["source"].unique()
    }

    preferred_title = _coalesce(
        [
            source_to_row[source]["event_title"]
            for source in ("dice", "songkick", "bandsintown")
            if source in source_to_row
        ]
    )
    preferred_venue = _coalesce(
        [
            source_to_row[source]["venue"]
            for source in ("dice", "songkick", "bandsintown")
            if source in source_to_row
        ]
    )
    preferred_city = _coalesce(
        [
            source_to_row[source]["raw_city"]
            for source in ("dice", "songkick", "bandsintown")
            if source in source_to_row
        ]
    )
    preferred_time = _coalesce(
        [
            source_to_row[source]["event_start_time"]
            for source in ("dice", "songkick", "bandsintown")
            if source in source_to_row
        ]
    )
    preferred_image = _coalesce(
        [
            source_to_row[source]["image_url"]
            for source in ("dice", "songkick", "bandsintown")
            if source in source_to_row
        ]
    )

    return {
        "merged_event_id": f"merged_{cluster_id:05d}",
        "event_date": _coalesce(cluster_df["event_date"].tolist()),
        "event_start_time": preferred_time,
        "venue": preferred_venue,
        "normalized_venue": _coalesce(cluster_df["normalized_venue"].tolist()),
        "city": preferred_city,
        "normalized_city": _coalesce(cluster_df["normalized_city"].tolist()),
        "event_title": preferred_title,
        "normalized_title": _normalize_text(preferred_title),
        "artists": " | ".join(artists),
        "artist_count": str(len(artists)),
        "sources_present": " | ".join(sorted(cluster_df["source"].unique())),
        "source_event_ids": json.dumps(
            {
                source: row["source_event_id"]
                for source, row in source_to_row.items()
                if _safe_text(row["source_event_id"])
            },
            ensure_ascii=True,
            sort_keys=True,
        ),
        "dice_url": _safe_text(source_to_row["dice"]["event_url"]) if "dice" in source_to_row else "",
        "songkick_url": _safe_text(source_to_row["songkick"]["event_url"]) if "songkick" in source_to_row else "",
        "bandsintown_url": _safe_text(source_to_row["bandsintown"]["event_url"]) if "bandsintown" in source_to_row else "",
        "image_url": preferred_image,
        "price": _safe_text(source_to_row["dice"]["price"]) if "dice" in source_to_row else "",
        "category": _safe_text(source_to_row["dice"]["category"]) if "dice" in source_to_row else "",
        "description": _safe_text(source_to_row["dice"]["description"]) if "dice" in source_to_row else "",
        "rsvp_count": _safe_text(source_to_row["bandsintown"]["rsvp_count"]) if "bandsintown" in source_to_row else "",
        "call_to_action": _safe_text(source_to_row["bandsintown"]["call_to_action"]) if "bandsintown" in source_to_row else "",
        "scraped_at_latest": max(cluster_df["scraped_at"].tolist()),
        "source_row_count": str(len(cluster_df)),
    }


def _cluster_matches(
    normalized_df: pd.DataFrame,
    approved_matches: list[tuple[str, str, MatchResult]],
) -> tuple[pd.DataFrame, pd.DataFrame]:
    ids = normalized_df["source_row_id"].tolist()
    dsu = DisjointSet(ids)
    match_reason_rows = []

    for left_id, right_id, result in approved_matches:
        dsu.union(left_id, right_id)
        match_reason_rows.append(
            {
                "left_source_row_id": left_id,
                "right_source_row_id": right_id,
                "score": result.score,
                "reasons": " | ".join(result.reasons),
            }
        )

    cluster_map: dict[str, list[str]] = defaultdict(list)
    for source_row_id in ids:
        cluster_map[dsu.find(source_row_id)].append(source_row_id)

    merged_rows = []
    for cluster_index, source_row_ids in enumerate(cluster_map.values(), start=1):
        cluster_df = normalized_df[normalized_df["source_row_id"].isin(source_row_ids)].copy()
        merged_rows.append(_merge_cluster(cluster_df, cluster_index))

    return pd.DataFrame(merged_rows), pd.DataFrame(match_reason_rows)


def _serialize_list_column(df: pd.DataFrame, column: str) -> pd.DataFrame:
    result = df.copy()
    result[column] = result[column].apply(lambda value: " | ".join(value) if isinstance(value, list) else value)
    return result


def main() -> int:
    parser = _build_parser()
    args = parser.parse_args()

    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    blocked_venues = _load_venue_blacklist(Path(args.venue_blacklist))
    blocked_cities = _load_venue_blacklist(Path(args.city_blacklist))
    venue_aliases = _load_venue_aliases(Path(args.venue_aliases))
    event_cutoff_time = _normalize_cutoff_time(args.event_cutoff_time)

    normalized_frames = [
        _prepare_bandsintown(Path(args.bandsintown), venue_aliases),
        _prepare_dice(Path(args.dice), venue_aliases),
        _prepare_songkick(Path(args.songkick), venue_aliases),
    ]
    normalized_frames, excluded_df = _apply_venue_blacklist(
        normalized_frames, blocked_venues
    )
    normalized_frames, city_excluded_df = _apply_city_blacklist(
        normalized_frames, blocked_cities
    )
    normalized_frames, cutoff_excluded_df = _apply_event_cutoff(
        normalized_frames, event_cutoff_time
    )
    excluded_df = _combine_excluded_frames(
        excluded_df, city_excluded_df, cutoff_excluded_df
    )
    normalized_df = pd.concat(normalized_frames, ignore_index=True)
    normalized_df = normalized_df.sort_values(
        by=["event_date", "normalized_venue", "event_start_time", "source"],
        kind="stable",
    ).reset_index(drop=True)

    approved_matches, review_rows = _build_matches(
        normalized_df, args.match_threshold, args.review_threshold
    )
    if args.interactive_review:
        approved_matches.extend(_interactive_review(review_rows))
    merged_df, match_log_df = _cluster_matches(normalized_df, approved_matches)
    merged_df = merged_df.sort_values(
        by=["event_date", "normalized_venue", "event_start_time"],
        kind="stable",
    ).reset_index(drop=True)

    normalized_export = _serialize_list_column(normalized_df, "normalized_artists")
    normalized_export.to_csv(output_dir / "normalized_events.csv", index=False)
    if not excluded_df.empty:
        excluded_export = _serialize_list_column(excluded_df, "normalized_artists")
        excluded_export.to_csv(output_dir / "excluded_events.csv", index=False)

    merged_df.to_csv(output_dir / "merged_events.csv", index=False)
    pd.DataFrame(review_rows).to_csv(output_dir / "review_candidates.csv", index=False)
    match_log_df.to_csv(output_dir / "match_log.csv", index=False)

    print(f"Normalized rows: {len(normalized_df)}")
    print(f"Excluded rows: {len(excluded_df)}")
    print(f"Merged events: {len(merged_df)}")
    print(f"Auto-match links: {len(approved_matches)}")
    print(f"Review candidates: {len(review_rows)}")
    print(f"Output directory: {output_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
