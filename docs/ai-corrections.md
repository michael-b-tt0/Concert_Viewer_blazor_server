# AI event corrections

Set the following server-side settings in `appsettings.json` (this repository ignores that file), or use equivalent environment variables such as `Tavily__ApiKey`:

```json
"Tavily": {
  "ApiKey": "your-key",
  "Model": "mini",
  "PollSeconds": 5,
  "TimeoutSeconds": 600
}
```

On an event, select **Research corrections with AI**. Research runs in a hosted background service; the page shows progress and opens a before/after comparison. Choose **Accept all changes** or **Reject**. The event date is fixed. Empty research fields preserve existing values; the researched performer list replaces the event's associations, including removing all associations if the list is empty. Shared artist records are never deleted.

Tavily returns artist names only; the application resolves music links separately. YouTube Music enrichment searches for each artist by name using the installed .NET package, selects the first matching channel in search order after name normalization, then searches tracks and filters by that channel. It merges up to three distinct matching song links in search order into existing social links and updates the artist's dedicated YouTube channel URL. These are search results, not an official top-songs ranking. Missing artist matches, no matching tracks, and failed lookups preserve existing links and show a warning. Artists sharing the same name cannot always be distinguished from names alone, so review the proposed links before accepting. Artist metadata changes affect other events using those artists.

Link merging counts the artist's existing YouTube Music links and adds new ones only until the total reaches three. Repeated corrections cannot add more after that limit. Existing links are retained, including any already above the limit, and other social platforms are unaffected.

One process-wide gate covers research, the pending review, acceptance, app event deletion, and the entire Python scraper/importer process. Browser refresh and navigation preserve the in-memory job; the review is available on both the concerts and scraper pages. A server restart discards it. Closing the browser does not release a pending review: return and accept or reject it. Failed research releases the gate. Server shutdown/timeouts stop local polling but may not cancel the remote Tavily task. Submission is never automatically retried to avoid duplicate paid tasks.

This gate covers writes through this application, not external scripts or direct database edits. It assumes one application server process. The existing Python import logic is unchanged and can overwrite approved corrections on later runs. The canonical import key remains unchanged by corrections.

Acceptance uses a database transaction. Failed saves retain the review and lock for retry or rejection. No database migration or job-history tables are required.

Open the browser developer tools Console and filter for `[AI corrections]` to follow progress. Messages include the event and job IDs, Tavily submission and polling status, artist enrichment results, review readiness, and save/reject outcomes. Warnings use `console.warn`; errors use `console.error`, including exception details and HTTP status codes where available. Diagnostics reach the browser through the existing one-second UI refresh. The server keeps the most recent 512 entries in memory, so refresh replays available history; navigation within the same page session avoids duplicates. API keys, request headers, full payloads, and server stack traces are not logged to the browser.

Run offline behavioural checks with:

```powershell
dotnet run --project tests/Corrections/Corrections.csproj
```

These use an in-memory SQLite database, fake Tavily responses, and fake YouTube Music searches, with no external API calls or modifications to `concerts.db`.
