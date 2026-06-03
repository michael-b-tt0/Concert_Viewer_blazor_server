#!/usr/bin/env python3
"""Run the concert scraper-to-database pipeline with one shared CLI."""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path


ROOT_DIR = Path(__file__).resolve().parent.parent
PROJECT_DIR = ROOT_DIR.parent
MASTER_DIR = Path(__file__).resolve().parent
OUTPUT_DIR = MASTER_DIR / "output"
CLEAN_AND_COMBINE_SCRIPT = MASTER_DIR / "clean_and_combine.py"
IMPORT_MERGED_EVENTS_SCRIPT = MASTER_DIR / "import_merged_events.py"
BACKFILL_ARTIST_METADATA_SCRIPT = (
    MASTER_DIR / "backfill_artist_lastfm_metadata_ytmusic.py"
)
DEFAULT_DB_PATH = PROJECT_DIR / "concerts.db"
DEFAULT_APP_SETTINGS_PATH = PROJECT_DIR / "appsettings.json"
MERGED_EVENTS_PATH = OUTPUT_DIR / "merged_events.csv"
DEFAULT_EVENT_CUTOFF_TIME = "16:00"


@dataclass(frozen=True)
class ScraperDefinition:
    """Describe how to invoke one scraper."""

    name: str
    folder: Path
    cities: tuple[str, ...]
    output_stem: str


SCRAPERS: dict[str, ScraperDefinition] = {
    "bandsintown": ScraperDefinition(
        name="bandsintown",
        folder=ROOT_DIR / "bandsintown-scraper",
        cities=("london",),
        output_stem="bandsintown_events",
    ),
    "dice": ScraperDefinition(
        name="dice",
        folder=ROOT_DIR / "dice-scraper",
        cities=("berlin", "london", "los-angeles", "new-york", "paris"),
        output_stem="dice_events",
    ),
    "songkick": ScraperDefinition(
        name="songkick",
        folder=ROOT_DIR / "songkick-scraper",
        cities=("london",),
        output_stem="songkick_events",
    ),
}


def _validate_date(value: str) -> str:
    """Validate CLI date input."""
    try:
        datetime.strptime(value, "%Y-%m-%d")
    except ValueError as exc:
        raise argparse.ArgumentTypeError(
            f"Invalid date '{value}'. Use YYYY-MM-DD format."
        ) from exc
    return value


def _validate_time(value: str) -> str:
    """Validate CLI time input."""
    try:
        datetime.strptime(value, "%H:%M")
    except ValueError as exc:
        raise argparse.ArgumentTypeError(
            f"Invalid time '{value}'. Use HH:MM format."
        ) from exc
    return value


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=(
            "Run multiple concert scrapers sequentially, clean and merge the "
            "outputs, import merged events into SQLite, then backfill artist metadata."
        )
    )

    parser.add_argument(
        "--sites",
        nargs="+",
        choices=tuple(SCRAPERS.keys()),
        default=list(SCRAPERS.keys()),
        help="Scrapers to run (default: all three)",
    )
    parser.add_argument(
        "--city",
        type=str,
        help="City to scrape for the selected sites",
    )
    parser.add_argument(
        "--list-cities",
        action="store_true",
        help="List supported cities and exit",
    )
    parser.add_argument(
        "--date",
        type=_validate_date,
        help="Scrape a single date (YYYY-MM-DD)",
    )
    parser.add_argument(
        "--from-date",
        type=_validate_date,
        help="Start date filter in YYYY-MM-DD format",
    )
    parser.add_argument(
        "--until-date",
        type=_validate_date,
        help="End date filter in YYYY-MM-DD format",
    )
    parser.add_argument(
        "--json",
        action="store_true",
        help="Also export JSON files beside the CSV files",
    )
    parser.add_argument(
        "--api-key",
        type=str,
        help="ScrapingAnt API key (or set SCRAPINGANT_API_KEY env var)",
    )
    parser.add_argument(
        "-v",
        "--verbose",
        action="store_true",
        help="Enable verbose output in the master runner and child scrapers",
    )

    parser.add_argument(
        "--dice-category",
        choices=("popular", "gigs", "dj", "podcast", "talks"),
        help="Run DICE for one category only",
    )
    parser.add_argument(
        "--dice-categories",
        nargs="+",
        choices=("popular", "gigs", "dj", "podcast", "talks"),
        help="Run DICE for multiple categories",
    )
    parser.add_argument(
        "--dice-all-categories",
        action="store_true",
        help="Run DICE across every available category",
    )
    parser.add_argument(
        "--event-cutoff-time",
        type=_validate_time,
        default=DEFAULT_EVENT_CUTOFF_TIME,
        help=(
            "Exclude events starting before this HH:MM time when cleaning "
            f"(default: {DEFAULT_EVENT_CUTOFF_TIME})"
        ),
    )
    parser.add_argument(
        "--db-path",
        default=str(DEFAULT_DB_PATH),
        help=f"SQLite database path for import and backfill (default: {DEFAULT_DB_PATH})",
    )
    parser.add_argument(
        "--app-settings-path",
        default=str(DEFAULT_APP_SETTINGS_PATH),
        help=(
            "App settings JSON path for Last.fm credentials "
            f"(default: {DEFAULT_APP_SETTINGS_PATH})"
        ),
    )
    parser.add_argument(
        "--backfill-limit",
        type=int,
        default=0,
        help="Maximum artists to backfill after import; 0 means no limit",
    )
    parser.add_argument(
        "--backfill-overwrite",
        action="store_true",
        help="Re-check artists even when metadata and social links already exist",
    )
    parser.add_argument(
        "--backfill-dry-run",
        action="store_true",
        help="Run the artist metadata backfill without writing database changes",
    )

    return parser


def _selected_scrapers(site_names: list[str]) -> list[ScraperDefinition]:
    return [SCRAPERS[site_name] for site_name in site_names]


def _print_cities(selected: list[ScraperDefinition]) -> None:
    city_sets = [set(scraper.cities) for scraper in selected]
    common_cities = sorted(set.intersection(*city_sets)) if city_sets else []

    print("Common cities for selected scrapers:")
    if common_cities:
        for city in common_cities:
            print(f"  - {city}")
    else:
        print("  (none)")

    print("\nCities by scraper:")
    for scraper in selected:
        print(f"  - {scraper.name}: {', '.join(scraper.cities)}")


def _normalize_dates(
    parser: argparse.ArgumentParser, args: argparse.Namespace
) -> tuple[str | None, str | None]:
    if args.date and (args.from_date or args.until_date):
        parser.error("--date cannot be used together with --from-date or --until-date")

    from_date = args.date or args.from_date
    until_date = args.date or args.until_date

    if from_date and not until_date:
        until_date = from_date
    elif until_date and not from_date:
        from_date = until_date

    if from_date and until_date and from_date > until_date:
        parser.error("--from-date must be earlier than or equal to --until-date")

    return from_date, until_date


def _validate_city(
    parser: argparse.ArgumentParser, city: str | None, selected: list[ScraperDefinition]
) -> str:
    if not city:
        parser.error("--city is required unless you are using --list-cities")

    unsupported = [scraper.name for scraper in selected if city not in scraper.cities]
    if unsupported:
        supported_lines = [
            f"{scraper.name}: {', '.join(scraper.cities)}" for scraper in selected
        ]
        parser.error(
            f"City '{city}' is not supported by all selected scrapers. "
            f"Selected scraper support -> {'; '.join(supported_lines)}"
        )

    return city


def _require_api_key(
    parser: argparse.ArgumentParser, explicit_api_key: str | None
) -> str:
    api_key = explicit_api_key or os.environ.get("SCRAPINGANT_API_KEY")
    if not api_key:
        parser.error(
            "ScrapingAnt API key is required. Use --api-key or set "
            "SCRAPINGANT_API_KEY."
        )
    return api_key


def _require_dates_for_sites(
    parser: argparse.ArgumentParser,
    selected: list[ScraperDefinition],
    from_date: str | None,
    until_date: str | None,
) -> None:
    requires_dates = {"bandsintown", "songkick"}
    if any(scraper.name in requires_dates for scraper in selected):
        if not (from_date and until_date):
            parser.error(
                "Bandsintown and Songkick require a date filter. "
                "Use --date or --from-date/--until-date."
            )


def _build_command(
    scraper: ScraperDefinition,
    args: argparse.Namespace,
    city: str,
    from_date: str | None,
    until_date: str | None,
) -> tuple[list[str], Path]:
    output_path = OUTPUT_DIR / f"{scraper.output_stem}.csv"
    command = [sys.executable, "main.py", "--city", city, "--output", str(output_path)]

    if from_date and until_date:
        command.extend(["--from-date", from_date, "--until-date", until_date])

    if args.json:
        command.append("--json")

    if args.verbose:
        command.append("--verbose")

    if args.api_key:
        command.extend(["--api-key", args.api_key])

    if scraper.name == "dice":
        if args.dice_category:
            command.extend(["--category", args.dice_category])
        elif args.dice_categories:
            command.append("--categories")
            command.extend(args.dice_categories)
        elif args.dice_all_categories:
            command.append("--all-categories")

    return command, output_path


def _run_scraper(
    scraper: ScraperDefinition,
    command: list[str],
    env: dict[str, str],
    verbose: bool,
) -> int:
    print(f"\nRunning {scraper.name} scraper...")
    if verbose:
        print(f"Working directory: {scraper.folder}")
        print(f"Command: {' '.join(command)}")

    completed = subprocess.run(
        command,
        cwd=scraper.folder,
        env=env,
        check=False,
    )
    return completed.returncode


def _run_clean_and_combine(args: argparse.Namespace, verbose: bool) -> int:
    command = [
        sys.executable,
        str(CLEAN_AND_COMBINE_SCRIPT),
        "--bandsintown",
        str(OUTPUT_DIR / f"{SCRAPERS['bandsintown'].output_stem}.csv"),
        "--dice",
        str(OUTPUT_DIR / f"{SCRAPERS['dice'].output_stem}.csv"),
        "--songkick",
        str(OUTPUT_DIR / f"{SCRAPERS['songkick'].output_stem}.csv"),
        "--output-dir",
        str(OUTPUT_DIR),
        "--event-cutoff-time",
        args.event_cutoff_time,
    ]

    print("\nRunning clean and combine step...")
    if verbose:
        print(f"Working directory: {MASTER_DIR}")
        print(f"Command: {' '.join(command)}")

    completed = subprocess.run(
        command,
        cwd=MASTER_DIR,
        check=False,
    )
    return completed.returncode


def _run_import_merged_events(args: argparse.Namespace, verbose: bool) -> int:
    command = [
        sys.executable,
        str(IMPORT_MERGED_EVENTS_SCRIPT),
        "--db-path",
        args.db_path,
        "--csv-path",
        str(MERGED_EVENTS_PATH),
    ]

    print("\nImporting merged events into the database...")
    if verbose:
        print(f"Working directory: {MASTER_DIR}")
        print(f"Command: {' '.join(command)}")

    completed = subprocess.run(
        command,
        cwd=MASTER_DIR,
        check=False,
    )
    return completed.returncode


def _run_artist_metadata_backfill(args: argparse.Namespace, verbose: bool) -> int:
    command = [
        sys.executable,
        str(BACKFILL_ARTIST_METADATA_SCRIPT),
        "--db-path",
        args.db_path,
        "--app-settings-path",
        args.app_settings_path,
        "--limit",
        str(args.backfill_limit),
    ]

    if args.backfill_overwrite:
        command.append("--overwrite")

    if args.backfill_dry_run:
        command.append("--dry-run")

    print("\nBackfilling artist Last.fm and YouTube Music metadata...")
    if verbose:
        print(f"Working directory: {MASTER_DIR}")
        print(f"Command: {' '.join(command)}")

    completed = subprocess.run(
        command,
        cwd=MASTER_DIR,
        check=False,
    )
    return completed.returncode


def main() -> int:
    parser = _build_parser()
    args = parser.parse_args()

    selected = _selected_scrapers(args.sites)

    if args.list_cities:
        _print_cities(selected)
        return 0

    if args.dice_category and args.dice_categories:
        parser.error("--dice-category cannot be used together with --dice-categories")

    dice_mode_count = sum(
        bool(value)
        for value in (
            args.dice_category,
            args.dice_categories,
            args.dice_all_categories,
        )
    )
    if dice_mode_count > 1:
        parser.error(
            "Use only one of --dice-category, --dice-categories, or "
            "--dice-all-categories"
        )

    from_date, until_date = _normalize_dates(parser, args)
    _require_dates_for_sites(parser, selected, from_date, until_date)
    city = _validate_city(parser, args.city, selected)
    api_key = _require_api_key(parser, args.api_key)

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    env = os.environ.copy()
    env["SCRAPINGANT_API_KEY"] = api_key

    failures: list[str] = []

    for scraper in selected:
        command, output_path = _build_command(scraper, args, city, from_date, until_date)
        return_code = _run_scraper(scraper, command, env, args.verbose)
        if return_code == 0:
            print(f"{scraper.name} finished successfully: {output_path}")
        else:
            failures.append(scraper.name)
            print(f"{scraper.name} failed with exit code {return_code}")

    if failures:
        print("\nRun summary:")
        succeeded = [scraper.name for scraper in selected if scraper.name not in failures]
        if succeeded:
            print(f"  Succeeded: {', '.join(succeeded)}")
        print(f"  Failed: {', '.join(failures)}")
        print(f"  Output directory: {OUTPUT_DIR}")
        return 1

    clean_return_code = _run_clean_and_combine(args, args.verbose)
    if clean_return_code == 0:
        import_return_code = _run_import_merged_events(args, args.verbose)
    else:
        import_return_code = None

    if import_return_code == 0:
        backfill_return_code = _run_artist_metadata_backfill(args, args.verbose)
    else:
        backfill_return_code = None

    print("\nRun summary:")
    if clean_return_code != 0:
        print(f"  All scrapers completed successfully.")
        print(f"  Clean and combine failed with exit code {clean_return_code}")
        print(f"  Output directory: {OUTPUT_DIR}")
        return clean_return_code

    if import_return_code != 0:
        print(f"  All scrapers completed successfully.")
        print(f"  Clean and combine completed successfully.")
        print(f"  Import merged events failed with exit code {import_return_code}")
        print(f"  Output directory: {OUTPUT_DIR}")
        print(f"  Database: {args.db_path}")
        return int(import_return_code)

    if backfill_return_code != 0:
        print(f"  All scrapers completed successfully.")
        print(f"  Clean and combine completed successfully.")
        print(f"  Import merged events completed successfully.")
        print(f"  Artist metadata backfill failed with exit code {backfill_return_code}")
        print(f"  Output directory: {OUTPUT_DIR}")
        print(f"  Database: {args.db_path}")
        return int(backfill_return_code)

    print(f"  All scrapers completed successfully.")
    print(f"  Clean and combine completed successfully.")
    print(f"  Import merged events completed successfully.")
    print(f"  Artist metadata backfill completed successfully.")
    print(f"  Output directory: {OUTPUT_DIR}")
    print(f"  Database: {args.db_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
