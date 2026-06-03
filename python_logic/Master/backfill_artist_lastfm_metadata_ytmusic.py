#!/usr/bin/env python3
"""Backfill artist metadata from Last.fm and YouTube Music links."""

from __future__ import annotations

import argparse
import json
import os
import re
import sqlite3
import sys
import time
import unicodedata
from pathlib import Path
from typing import Any

import pylast
from ytmusicapi import YTMusic


MASTER_DIR = Path(__file__).resolve().parent
PROJECT_DIR = MASTER_DIR.parent.parent
DEFAULT_DB_PATH = PROJECT_DIR / "concerts.db"
DEFAULT_APP_SETTINGS_PATH = PROJECT_DIR / "appsettings.json"
DEFAULT_TAG_LIMIT = 7
DEFAULT_ALBUM_LIMIT = 3
DEFAULT_SLEEP_SECONDS = 0.1
YOUTUBE_MUSIC_TOP_SONG_LIMIT = 3
YOUTUBE_MUSIC_PLATFORM = "youtube_music"


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=(
            "Look up artist metadata with Last.fm and update artists.sociallinks "
            "and artists.youtube_url with confirmed YouTube Music artist matches."
        )
    )
    parser.add_argument(
        "--db-path",
        default=str(DEFAULT_DB_PATH),
        help=f"SQLite database path (default: {DEFAULT_DB_PATH})",
    )
    parser.add_argument(
        "--app-settings-path",
        default=str(DEFAULT_APP_SETTINGS_PATH),
        help=f"App settings JSON path (default: {DEFAULT_APP_SETTINGS_PATH})",
    )
    parser.add_argument(
        "--limit",
        type=int,
        default=0,
        help="Maximum number of artists to process; 0 means no limit",
    )
    parser.add_argument(
        "--overwrite",
        action="store_true",
        help="Re-check artists even when Last.fm metadata and social links already exist",
    )
    parser.add_argument(
        "--replace-existing-sociallinks",
        action="store_true",
        help="Replace artists.sociallinks instead of merging YouTube Music links into it",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Print what would be updated without writing to the database",
    )
    parser.add_argument(
        "--tag-limit",
        type=int,
        default=DEFAULT_TAG_LIMIT,
        help=f"Maximum number of Last.fm top tags to store (default: {DEFAULT_TAG_LIMIT})",
    )
    parser.add_argument(
        "--album-limit",
        type=int,
        default=DEFAULT_ALBUM_LIMIT,
        help=f"Maximum number of Last.fm top albums to store and search (default: {DEFAULT_ALBUM_LIMIT})",
    )
    parser.add_argument(
        "--sleep-seconds",
        type=float,
        default=DEFAULT_SLEEP_SECONDS,
        help=(
            "Delay between artist lookups in seconds to keep calls sequential and gentle "
            f"(default: {DEFAULT_SLEEP_SECONDS})"
        ),
    )
    return parser


def _load_app_settings(path: Path) -> dict[str, Any]:
    if not path.exists():
        return {}
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"Invalid JSON in app settings file {path}: {exc}") from exc


def _require_lastfm_credentials(app_settings_path: Path) -> tuple[str, str]:
    app_settings = _load_app_settings(app_settings_path)
    python_settings = app_settings.get("PythonSettings", {})
    lastfm_settings = (
        python_settings.get("LastFm", {})
        if isinstance(python_settings, dict)
        else {}
    )

    api_key = str(lastfm_settings.get("ApiKey", "")).strip()
    api_secret = str(lastfm_settings.get("ApiSecret", "")).strip()

    if not api_key:
        api_key = str(app_settings.get("lastfm_api_key", "")).strip()
    if not api_secret:
        api_secret = str(app_settings.get("lastfm_api_secret", "")).strip()

    if not api_key:
        api_key = os.environ.get("LASTFM_API_KEY", "").strip()
    if not api_secret:
        api_secret = os.environ.get("LASTFM_API_SECRET", "").strip()

    if api_key and api_secret:
        return api_key, api_secret

    raise RuntimeError(
        "Set `PythonSettings:LastFm:ApiKey` and `PythonSettings:LastFm:ApiSecret` "
        "in appsettings.json "
        "or LASTFM_API_KEY / LASTFM_API_SECRET in the environment."
    )


def _fetch_artists(
    connection: sqlite3.Connection,
    overwrite: bool,
    limit: int,
) -> list[sqlite3.Row]:
    query = """
        SELECT
            id,
            name,
            formal_name,
            lastfmpage,
            artist_tags,
            top_albums,
            sociallinks,
            youtube_url
        FROM artists
    """
    conditions: list[str] = []
    params: list[Any] = []

    if not overwrite:
        conditions.append(
            "("
            "sociallinks IS NULL OR TRIM(sociallinks) = '' OR TRIM(sociallinks) = '[]'"
            ")"
        )
        conditions.append(
            "("
            "formal_name IS NULL OR TRIM(formal_name) = '' OR "
            "lastfmpage IS NULL OR TRIM(lastfmpage) = '' OR "
            "artist_tags IS NULL OR TRIM(artist_tags) = '' OR TRIM(artist_tags) = '[]' OR "
            "top_albums IS NULL OR TRIM(top_albums) = '' OR TRIM(top_albums) = '[]' OR "
            "youtube_url IS NULL OR TRIM(youtube_url) = ''"
            ")"
        )

    if conditions:
        query += " WHERE " + " AND ".join(conditions)

    query += " ORDER BY name COLLATE NOCASE ASC"
    if limit > 0:
        query += " LIMIT ?"
        params.append(limit)

    return connection.execute(query, params).fetchall()


def _get_canonical_artist(network: pylast.LastFMNetwork, artist_name: str) -> tuple[pylast.Artist, str]:
    artist = network.get_artist(artist_name)
    corrected_name = artist.get_correction()
    if corrected_name:
        return network.get_artist(corrected_name), corrected_name
    return artist, artist_name


def _get_artist_tags(artist: pylast.Artist, limit: int) -> list[str]:
    tags: list[str] = []
    seen: set[str] = set()

    for top_item in artist.get_top_tags(limit=limit):
        tag_name = str(top_item.item.get_name()).strip()
        normalized = tag_name.lower()
        if tag_name and normalized not in seen:
            tags.append(tag_name)
            seen.add(normalized)

    return tags


def _get_top_albums(artist: pylast.Artist, limit: int) -> list[dict[str, str]]:
    albums: list[dict[str, str]] = []
    seen: set[str] = set()

    for top_item in artist.get_top_albums( limit=limit):
        album = top_item.item
        title = str(album.get_title() or "").strip()
        if not title:
            continue

        normalized = title.lower()
        if normalized in seen:
            continue

        try:
            album_url = str(album.get_url() or "").strip()
        except pylast.WSError:
            album_url = ""

        albums.append({"title": title, "lastfm_album_url": album_url})
        seen.add(normalized)

    return albums


def _parse_json_sociallink_list(raw_value: object) -> list[dict[str, str]]:
    if raw_value is None:
        return []
    text = str(raw_value).strip()
    if not text:
        return []
    try:
        parsed = json.loads(text)
    except json.JSONDecodeError:
        return []
    if not isinstance(parsed, list):
        return []

    links: list[dict[str, str]] = []
    seen: set[str] = set()
    for item in parsed:
        if not isinstance(item, dict):
            continue
        url = str(item.get("url", "")).strip()
        platform = str(item.get("platform", "unknown")).strip() or "unknown"
        normalized = url.lower()
        if url and normalized not in seen:
            link = {"platform": platform, "url": url}
            label = str(item.get("label", "")).strip()
            if label:
                link["label"] = label
            links.append(link)
            seen.add(normalized)
    return links


def _merge_sociallink_lists(*groups: list[dict[str, str]]) -> list[dict[str, str]]:
    merged: list[dict[str, str]] = []
    seen: set[str] = set()
    for group in groups:
        for item in group:
            if not isinstance(item, dict):
                continue
            url = str(item.get("url", "")).strip()
            platform = str(item.get("platform", "unknown")).strip() or "unknown"
            normalized = url.lower()
            if url and normalized not in seen:
                link = {"platform": platform, "url": url}
                label = str(item.get("label", "")).strip()
                if label:
                    link["label"] = label
                merged.append(link)
                seen.add(normalized)
    return merged


def _normalize_text(value: object) -> str:
    text = unicodedata.normalize("NFKD", str(value or ""))
    text = text.encode("ascii", "ignore").decode("ascii")
    text = text.casefold().replace("&", " and ")
    return re.sub(r"[^a-z0-9]+", " ", text).strip()


def _artists_match(expected_artist_name: str, candidate_artist_name: object) -> bool:
    expected = _normalize_text(expected_artist_name)
    candidate = _normalize_text(candidate_artist_name)
    return bool(expected and candidate and expected == candidate)


def _extract_ytmusic_result_artists(result: dict[str, Any]) -> list[dict[str, str]]:
    raw_artists = result.get("artists")
    if isinstance(raw_artists, list):
        artists = []
        for raw_artist in raw_artists:
            if not isinstance(raw_artist, dict):
                continue
            name = str(raw_artist.get("name", "")).strip()
            browse_id = str(raw_artist.get("id", "") or raw_artist.get("browseId", "")).strip()
            if name:
                artists.append({"name": name, "browse_id": browse_id})
        return artists

    result_type = str(result.get("resultType", "")).strip().lower()
    category = str(result.get("category", "")).strip().lower()
    if result_type == "artist" or category == "artists":
        name = str(result.get("title", "")).strip()
        browse_id = str(result.get("browseId", "") or result.get("id", "")).strip()
        if name:
            return [{"name": name, "browse_id": browse_id}]

    artist_name = str(result.get("artist", "")).strip()
    artist_browse_id = str(result.get("artistId", "") or result.get("artistBrowseId", "")).strip()
    if artist_name:
        return [{"name": artist_name, "browse_id": artist_browse_id}]

    return []


def _find_ytmusic_artist_browse_id_in_results(
    artist_name: str,
    results: list[Any],
) -> str:
    for result in results:
        if not isinstance(result, dict):
            continue

        for candidate_artist in _extract_ytmusic_result_artists(result):
            if not _artists_match(artist_name, candidate_artist["name"]):
                continue
            browse_id = candidate_artist["browse_id"].strip()
            if browse_id:
                return browse_id

    return ""


def _find_ytmusic_artist_browse_id(
    ytmusic: YTMusic,
    artist_name: str,
    albums: list[dict[str, str]],
) -> str:
    for album in albums:
        album_title = str(album.get("title", "")).strip()
        if not album_title:
            continue

        results = ytmusic.search(album_title, filter="albums", limit=10)
        browse_id = _find_ytmusic_artist_browse_id_in_results(artist_name, results)
        if browse_id:
            return browse_id

    return ""


def _watch_url_from_video_id(video_id: object) -> str:
    video_id_text = str(video_id or "").strip()
    if not video_id_text:
        return ""
    return f"https://music.youtube.com/watch?v={video_id_text}"


def _song_link_from_album_result(
    ytmusic: YTMusic,
    result: dict[str, Any],
    fallback_artist_name: str,
) -> dict[str, str]:
    browse_id = str(result.get("browseId") or "").strip()
    if not browse_id:
        return {}

    try:
        album_details = ytmusic.get_album(browse_id)
    except Exception:
        return {}
    if not isinstance(album_details, dict):
        return {}

    album_title = str(album_details.get("title") or result.get("title") or "").strip()
    tracks = album_details.get("tracks", [])
    if not isinstance(tracks, list):
        return {}

    for track in tracks:
        if not isinstance(track, dict):
            continue

        url = _watch_url_from_video_id(track.get("videoId"))
        if not url:
            continue

        title = str(track.get("title", "")).strip()
        artists = _extract_ytmusic_result_artists(track) or _extract_ytmusic_result_artists(result)
        artist_names = [artist["name"] for artist in artists if artist["name"]]
        artist_label = ", ".join(artist_names) or fallback_artist_name
        label_parts = [part for part in (artist_label, title, album_title) if part]
        return {
            "platform": YOUTUBE_MUSIC_PLATFORM,
            "url": url,
            "label": " - ".join(label_parts),
        }

    return {}


def _song_link_from_artist_result(
    ytmusic: YTMusic,
    result: dict[str, Any],
) -> dict[str, str]:
    browse_id = str(result.get("browseId") or result.get("id") or "").strip()
    if not browse_id:
        return {}

    try:
        artist_profile = ytmusic.get_artist(browse_id)
    except Exception:
        return {}
    if not isinstance(artist_profile, dict):
        return {}

    links = _get_ytmusic_top_song_links(artist_profile, limit=1)
    return links[0] if links else {}


def _sociallink_from_ytmusic_result(
    ytmusic: YTMusic,
    result: dict[str, Any],
    fallback_artist_name: str,
) -> dict[str, str]:
    video_id = str(result.get("videoId") or "").strip()
    if video_id:
        title = str(result.get("title", "")).strip()
        artists = _extract_ytmusic_result_artists(result)
        artist_names = [artist["name"] for artist in artists if artist["name"]]
        artist_label = ", ".join(artist_names) or fallback_artist_name
        album = result.get("album", {})
        album_name = str(album.get("name", "")).strip() if isinstance(album, dict) else ""
        label_parts = [part for part in (artist_label, title, album_name) if part]
        return {
            "platform": YOUTUBE_MUSIC_PLATFORM,
            "url": _watch_url_from_video_id(video_id),
            "label": " - ".join(label_parts),
        }

    result_type = str(result.get("resultType", "")).strip().lower()
    category = str(result.get("category", "")).strip().lower()
    if result_type == "artist" or category == "artists":
        return _song_link_from_artist_result(ytmusic, result)

    return _song_link_from_album_result(ytmusic, result, fallback_artist_name)


def _get_ytmusic_fallback_sociallinks(
    ytmusic: YTMusic,
    artist_name: str,
    albums: list[dict[str, str]],
) -> list[dict[str, str]]:
    links: list[dict[str, str]] = []
    for album in albums:
        album_title = str(album.get("title", "")).strip()
        if not album_title:
            continue

        results = ytmusic.search(f"{artist_name} {album_title}", limit=1)
        if not results or not isinstance(results[0], dict):
            continue

        link = _sociallink_from_ytmusic_result(ytmusic, results[0], fallback_artist_name=artist_name)
        if link:
            links.append(link)

    return _merge_sociallink_lists(links)


def _get_ytmusic_name_search_sociallinks(
    ytmusic: YTMusic,
    artist_name: str,
) -> list[dict[str, str]]:
    results = ytmusic.search(artist_name, limit=5)
    for result in results:
        if not isinstance(result, dict):
            continue
        link = _sociallink_from_ytmusic_result(ytmusic, result, fallback_artist_name=artist_name)
        if link:
            return [link]
    return []


def _get_ytmusic_top_song_links(
    artist_profile: dict[str, Any],
    limit: int = YOUTUBE_MUSIC_TOP_SONG_LIMIT,
) -> list[dict[str, str]]:
    songs = artist_profile.get("songs", {})
    if not isinstance(songs, dict):
        return []

    results = songs.get("results", [])
    if not isinstance(results, list):
        return []

    links: list[dict[str, str]] = []
    profile_name = str(artist_profile.get("name", "")).strip()
    for song in results[:limit]:
        if not isinstance(song, dict):
            continue
        url = _watch_url_from_video_id(song.get("videoId"))
        if not url:
            continue

        title = str(song.get("title", "")).strip()
        album = song.get("album", {})
        album_name = str(album.get("name", "")).strip() if isinstance(album, dict) else ""
        label_parts = [part for part in (profile_name, title, album_name) if part]
        links.append(
            {
                "platform": YOUTUBE_MUSIC_PLATFORM,
                "url": url,
                "label": " - ".join(label_parts),
            }
        )

    return _merge_sociallink_lists(links)


def _youtube_channel_url_from_artist_profile(
    artist_profile: dict[str, Any],
    artist_browse_id: str,
) -> str:
    channel_id = str(artist_profile.get("channelId", "")).strip()
    if not channel_id and artist_browse_id.startswith("UC"):
        channel_id = artist_browse_id
    if not channel_id:
        return ""
    return f"https://www.youtube.com/channel/{channel_id}"


def _get_ytmusic_artist_links(
    ytmusic: YTMusic,
    artist_name: str,
    albums: list[dict[str, str]],
    use_artist_name_fallback: bool = False,
) -> tuple[list[dict[str, str]], str]:
    artist_browse_id = _find_ytmusic_artist_browse_id(ytmusic, artist_name, albums)
    if not artist_browse_id:
        fallback_links = _get_ytmusic_fallback_sociallinks(ytmusic, artist_name, albums)
        if fallback_links:
            return fallback_links, ""
        if use_artist_name_fallback:
            return _get_ytmusic_name_search_sociallinks(ytmusic, artist_name), ""
        return [], ""

    artist_profile = ytmusic.get_artist(artist_browse_id)
    if not isinstance(artist_profile, dict):
        return [], ""

    youtube_url = _youtube_channel_url_from_artist_profile(artist_profile, artist_browse_id)
    top_song_links = _get_ytmusic_top_song_links(artist_profile)
    if top_song_links:
        return top_song_links, youtube_url

    fallback_links = _get_ytmusic_fallback_sociallinks(ytmusic, artist_name, albums)
    if fallback_links:
        return fallback_links, youtube_url

    if use_artist_name_fallback:
        return _get_ytmusic_name_search_sociallinks(ytmusic, artist_name), youtube_url

    return [], youtube_url


def _lookup_artist_metadata(
    network: pylast.LastFMNetwork,
    artist_name: str,
    tag_limit: int,
    album_limit: int,
) -> tuple[dict[str, str], bool]:
    try:
        canonical_artist, canonical_name = _get_canonical_artist(network, artist_name)
    except pylast.WSError:
        return (
            {
                "formal_name": artist_name,
                "lastfmpage": "",
                "artist_tags": "[]",
                "top_albums": "[]",
            },
            False,
        )

    lastfm_page = ""
    tags: list[str] = []
    albums: list[dict[str, str]] = []

    try:
        lastfm_page = str(canonical_artist.get_url() or "").strip()
    except pylast.WSError:
        lastfm_page = ""

    try:
        tags = _get_artist_tags(canonical_artist, limit=tag_limit)
    except pylast.WSError:
        tags = []

    try:
        albums = _get_top_albums(canonical_artist, limit=album_limit)
    except pylast.WSError:
        albums = []

    return (
        {
            "formal_name": canonical_name.strip() or artist_name,
            "lastfmpage": lastfm_page,
            "artist_tags": json.dumps(tags, ensure_ascii=True),
            "top_albums": json.dumps(albums, ensure_ascii=True),
        },
        True,
    )


def _update_artist_metadata(
    connection: sqlite3.Connection,
    artist_id: int,
    metadata: dict[str, str],
    sociallinks: list[dict[str, str]],
    youtube_url: str,
) -> None:
    connection.execute(
        """
        UPDATE artists
        SET
            formal_name = ?,
            lastfmpage = ?,
            artist_tags = ?,
            top_albums = ?,
            sociallinks = ?,
            youtube_url = ?,
            updated_at = CURRENT_TIMESTAMP
        WHERE id = ?
        """,
        (
            metadata["formal_name"],
            metadata["lastfmpage"],
            metadata["artist_tags"],
            metadata["top_albums"],
            json.dumps(sociallinks, ensure_ascii=True),
            youtube_url,
            artist_id,
        ),
    )


def main() -> int:
    parser = _build_parser()
    args = parser.parse_args()

    db_path = Path(args.db_path)
    app_settings_path = Path(args.app_settings_path)
    if not db_path.exists():
        parser.error(f"Database not found: {db_path}")
    if args.album_limit <= 0:
        parser.error("--album-limit must be greater than 0")

    api_key, api_secret = _require_lastfm_credentials(app_settings_path)
    network = pylast.LastFMNetwork(api_key=api_key, api_secret=api_secret)
    ytmusic = YTMusic()

    processed = 0
    updated = 0
    skipped = 0
    failed = 0

    with sqlite3.connect(db_path) as connection:
        connection.row_factory = sqlite3.Row
        rows = _fetch_artists(connection, overwrite=args.overwrite, limit=args.limit)

        for row in rows:
            artist_id = int(row["id"])
            artist_name = str(row["name"] or "").strip()
            existing_links = _parse_json_sociallink_list(row["sociallinks"])
            existing_youtube_url = str(row["youtube_url"] or "").strip()
            processed += 1

            if not artist_name:
                skipped += 1
                print(f"[{processed}] skipped artist_id={artist_id} reason='blank name'")
                continue

            try:
                metadata, has_lastfm_result = _lookup_artist_metadata(
                    network=network,
                    artist_name=artist_name,
                    tag_limit=args.tag_limit,
                    album_limit=args.album_limit,
                )
                raw_albums = json.loads(metadata["top_albums"])
                albums = [item for item in raw_albums if isinstance(item, dict)] if isinstance(raw_albums, list) else []

                ytmusic_links, found_youtube_url = _get_ytmusic_artist_links(
                    ytmusic=ytmusic,
                    artist_name=metadata["formal_name"] or artist_name,
                    albums=albums,
                    use_artist_name_fallback=(not has_lastfm_result or not albums),
                )
                final_youtube_url = (
                    found_youtube_url
                    if found_youtube_url and (args.overwrite or not existing_youtube_url)
                    else existing_youtube_url
                )
                final_links = (
                    _merge_sociallink_lists(ytmusic_links)
                    if args.replace_existing_sociallinks
                    else _merge_sociallink_lists(existing_links, ytmusic_links)
                )
            except Exception as exc:
                failed += 1
                print(
                    f"[{processed}] failed  artist_id={artist_id} name={artist_name!r} error={exc}",
                    file=sys.stderr,
                )
                if args.sleep_seconds > 0:
                    time.sleep(args.sleep_seconds)
                continue

            should_update = has_lastfm_result or bool(ytmusic_links) or bool(found_youtube_url)

            if not should_update:
                skipped += 1
                print(
                    f"[{processed}] skipped artist_id={artist_id} name={artist_name!r} "
                    "reason='no YouTube Music result found'"
                )
            else:
                action = "would_update" if args.dry_run else "updated"
                fallback = " artist_search_fallback=true" if not has_lastfm_result else ""
                no_ytmusic = " ytmusic_result=false" if not ytmusic_links else ""
                youtube_url_status = " youtube_url=false" if not found_youtube_url else " youtube_url=true"
                print(
                    f"[{processed}] {action} artist_id={artist_id} name={artist_name!r} "
                    f"formal_name={metadata['formal_name']!r} albums={len(albums)} "
                    f"new_youtube_links={len(ytmusic_links)} total_links={len(final_links)}"
                    f"{fallback}{no_ytmusic}{youtube_url_status}"
                )
                if not args.dry_run:
                    _update_artist_metadata(connection, artist_id, metadata, final_links, final_youtube_url)
                    connection.commit()
                    updated += 1

            if args.sleep_seconds > 0:
                time.sleep(args.sleep_seconds)

    print(f"Processed artists: {processed}")
    print(f"Updated artists: {updated}")
    print(f"Skipped artists: {skipped}")
    print(f"Failed lookups: {failed}")
    print(f"Database: {db_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
