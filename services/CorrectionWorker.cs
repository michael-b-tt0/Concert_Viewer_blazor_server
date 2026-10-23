using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Concert_Viewer.Data;
using Concert_Viewer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Concert_Viewer.Services;

public sealed record SocialLink(
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("label")] string? Label = null);
public sealed record ArtistCorrection(int? Id, string Name, string NormalizedName, string? SocialLinks, string? YouTubeUrl);
public sealed record EventCorrection(string? Title, string? Venue, string? City, string? Description, IReadOnlyList<ArtistCorrection> Artists);
public sealed record CorrectionJob(Guid Id, int EventId, string Date, string Status,
    EventCorrection? Before = null, EventCorrection? After = null,
    IReadOnlyList<string>? Warnings = null, IReadOnlyList<string>? Sources = null, string? Error = null);
public sealed record CorrectionDiagnostic(long Sequence, DateTimeOffset Timestamp, Guid JobId,
    int EventId, string Level, string Message);

public sealed class CorrectionWorker(
    OperationGate gate, IDbContextFactory<ConcertsContext> factory,
    TavilyResearchClient research, YouTubeMusicEnricher youtube,
    IConfiguration configuration, ILogger<CorrectionWorker> logger) : BackgroundService
{
    private readonly Channel<int> queue = Channel.CreateBounded<int>(1);
    private readonly SemaphoreSlim decisions = new(1, 1);
    private readonly object sync = new();
    private CorrectionJob? current;
    private IDisposable? lease;
    private long revision;
    private long diagnosticSequence;
    private readonly Queue<CorrectionDiagnostic> diagnostics = new();
    public CorrectionDiagnostic[] DiagnosticsAfter(long sequence)
    {
        lock (sync) return diagnostics.Where(entry => entry.Sequence > sequence).ToArray();
    }
    private void Report(CorrectionJob job, string level, string message)
    {
        // Only send explicit diagnostic messages to the browser, never headers, payloads or stack traces.
        var key = configuration["Tavily:ApiKey"];
        if (!string.IsNullOrEmpty(key)) message = message.Replace(key, "[redacted]", StringComparison.Ordinal);
        lock (sync)
        {
            diagnostics.Enqueue(new(++diagnosticSequence, DateTimeOffset.UtcNow, job.Id, job.EventId, level, message));
            while (diagnostics.Count > 512) diagnostics.Dequeue();
        }
    }
    private static string DescribeError(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message}" +
        (exception.InnerException is { } inner ? $" Inner error: {inner.GetType().Name}: {inner.Message}" : "");
    public long Revision => Interlocked.Read(ref revision);
    public CorrectionJob? Current { get { lock (sync) return current; } }
    private void Set(CorrectionJob? value) { lock (sync) current = value; }

    public void Start(int eventId, ClaimsPrincipal user)
    {
        RequireUser(user);
        lock (sync)
        {
            var acquired = gate.Enter("AI correction research and review");
            lease = acquired;
            current = new(Guid.NewGuid(), eventId, "", "Researching");
            Report(current, "info", "Research requested. Global operation lock acquired.");
            if (!queue.Writer.TryWrite(eventId))
            {
                acquired.Dispose(); lease = null; current = null;
                throw new InvalidOperationException("The research worker is busy.");
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var eventId in queue.Reader.ReadAllAsync(stoppingToken))
            {
                var job = Current!;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Tavily:TimeoutSeconds", 600), 10, 3600)));
                    var token = timeout.Token;
                    await using var db = await factory.CreateDbContextAsync(token);
                    var item = await db.CanonicalEvents.AsNoTracking().Include(e => e.EventArtists).ThenInclude(a => a.Artist)
                        .SingleAsync(e => e.Id == eventId, token);
                    var before = new EventCorrection(item.EventTitle, item.Venue, item.City, item.Description,
                        item.EventArtists.OrderBy(a => a.BillingPosition).Select(a => FromArtist(a.Artist!)).ToArray());
                    job = job with { Before = before, Date = item.EventDate };
                    Set(job);
                    Report(job, "info", $"Loaded event: {item.EventTitle} ({item.EventDate}). Starting web research.");
                    var output = await research.ResearchAsync(item, token, (level, message) => Report(job, level, message));
                    Validate(output);
                    Report(job, "info", $"Research output validated: {output.GetProperty("artists").GetArrayLength()} performers. Starting YouTube Music enrichment.");
                    Set(job with { Status = "Enriching artist links" });
                    var warnings = new List<string>();
                    var artists = new List<ArtistCorrection>();
                    var seen = new HashSet<string>();
                    foreach (var entry in output.GetProperty("artists").EnumerateArray())
                    {
                        var name = entry.GetProperty("name").GetString()!.Trim();
                        var normalized = NormalizeArtist(name);
                        if (!seen.Add(normalized)) continue;
                        var existing = await db.Artists.AsNoTracking().FirstOrDefaultAsync(a => a.NormalizedName == normalized, token);
                        var artist = existing is null ? new ArtistCorrection(null, name, normalized, "[]", null)
                            : FromArtist(existing) with { Name = name };
                        try
                        {
                            Report(job, "info", $"Looking up YouTube Music artist channel and tracks for {name}.");
                            using var artistTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                            artistTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                            var enriched = await youtube.EnrichAsync(name, artistTimeout.Token);
                            artist = artist with { SocialLinks = MergeLinks(artist.SocialLinks, enriched.Links), YouTubeUrl = enriched.ChannelUrl };
                            Report(job, "info", $"{name}: matched {enriched.Links.Count} tracks to the artist channel.");
                        }
                        catch (Exception ex) when (!token.IsCancellationRequested)
                        {
                            logger.LogWarning(ex, "YouTube Music enrichment failed for {Artist}", name);
                            Report(job, "warn", $"{name}: enrichment failed; existing links preserved. {DescribeError(ex)}");
                            warnings.Add($"{name}: YouTube Music enrichment unavailable; existing links preserved.");
                        }
                        artists.Add(artist);
                    }
                    var after = new EventCorrection(Value(output, "event_title", before.Title), Value(output, "venue", before.Venue),
                        Value(output, "city", before.City), Value(output, "description", before.Description), artists);
                    var sources = output.TryGetProperty("sources", out var sourceArray) && sourceArray.ValueKind == JsonValueKind.Array
                        ? sourceArray.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!)
                            .Where(IsWebUrl).Distinct().ToArray() : [];
                    Set(job with { Status = "Ready for review", After = after, Warnings = warnings, Sources = sources });
                    Report(job, "info", $"Comparison ready: {artists.Count} performers, {warnings.Count} warnings. Waiting for Accept all changes or Reject; operation lock remains held.");
                    // Keep the lease until a binary decision, even across browser refreshes.
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Correction research failed for event {EventId}", eventId);
                    Report(job, "error", $"Research failed. {DescribeError(ex)}");
                    Set(job with { Status = "Failed", Error = ex is OperationCanceledException
                        ? "Research timed out or the server stopped. No changes were made. Tavily may still finish its remote request."
                        : ex is HttpRequestException ? $"{ex.Message} No changes were made."
                        : ex.Message });
                    Release();
                    Report(job, "info", "No changes saved. Operation lock released.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { Release(); }
    }

    public async Task DecideAsync(Guid jobId, bool accept, ClaimsPrincipal user)
    {
        RequireUser(user);
        await decisions.WaitAsync();
        try
        {
            var job = Current;
            if (job?.Id != jobId || job.Status != "Ready for review" || job.After is null)
                throw new InvalidOperationException("This proposal is no longer available.");
            if (accept)
            {
                Report(job, "info", "Acceptance received. Saving event and artist changes in a transaction.");
                await using var db = await factory.CreateDbContextAsync();
                await using var transaction = await db.Database.BeginTransactionAsync();
                var item = await db.CanonicalEvents.Include(e => e.EventArtists).SingleAsync(e => e.Id == job.EventId);
                var proposed = job.After;
                item.EventTitle = proposed.Title; item.Venue = proposed.Venue;
                item.City = proposed.City; item.Description = proposed.Description;
                item.NormalizedTitle = NormalizeText(proposed.Title);
                item.NormalizedVenue = NormalizeText(proposed.Venue);
                item.NormalizedCity = NormalizeText(proposed.City);
                // CanonicalKey remains the import identity. Date and source URLs never change here.
                item.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
                var roles = item.EventArtists.ToDictionary(a => a.ArtistId, a => a.Role);
                db.EventArtists.RemoveRange(item.EventArtists);
                await db.SaveChangesAsync();
                for (var index = 0; index < proposed.Artists.Count; index++)
                {
                    var suggestion = proposed.Artists[index];
                    var artist = suggestion.Id.HasValue ? await db.Artists.SingleAsync(a => a.Id == suggestion.Id.Value)
                        : new Artist { NormalizedName = suggestion.NormalizedName, CreatedAt = item.UpdatedAt };
                    if (!suggestion.Id.HasValue) db.Artists.Add(artist);
                    artist.Name = suggestion.Name;
                    artist.SocialLinks = suggestion.SocialLinks;
                    artist.YouTubeUrl = suggestion.YouTubeUrl;
                    artist.UpdatedAt = item.UpdatedAt;
                    db.EventArtists.Add(new EventArtist { CanonicalEventId = item.Id, Artist = artist,
                        BillingPosition = index + 1, Role = suggestion.Id.HasValue ? roles.GetValueOrDefault(suggestion.Id.Value) : null,
                        CreatedAt = item.UpdatedAt });
                }
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                Interlocked.Increment(ref revision);
            }
            Set(null);
            Release();
            Report(job, "info", accept ? "Changes saved successfully. Operation lock released."
                : "Proposal rejected. No changes saved. Operation lock released.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply correction decision for job {JobId}", jobId);
            var job = Current;
            if (job is not null) Report(job, "error", $"Could not apply decision; review remains available. {DescribeError(ex)}");
            throw;
        }
        finally { decisions.Release(); }
    }

    public void DismissFailure(Guid id)
    {
        lock (sync) if (current?.Id == id && current.Status == "Failed") current = null;
    }
    private void Release() { lock (sync) { lease?.Dispose(); lease = null; } }
    private static void RequireUser(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) throw new UnauthorizedAccessException("Sign in to research or accept corrections.");
    }
    private static ArtistCorrection FromArtist(Artist artist) => new(artist.Id, artist.Name, artist.NormalizedName, artist.SocialLinks, artist.YouTubeUrl);
    private static string? Value(JsonElement output, string field, string? fallback) =>
        string.IsNullOrWhiteSpace(output.GetProperty(field).GetString()) ? fallback : output.GetProperty(field).GetString()!.Trim();
    public static bool IsWebUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
    public static string NormalizeText(string? value)
    {
        var ascii = new string((value ?? "").Normalize(NormalizationForm.FormKD).Where(c => c <= 127).ToArray());
        var text = ascii.ToLowerInvariant().Replace("&", " and ").Replace("'", "");
        return Regex.Replace(Regex.Replace(text, "[^a-z0-9\\s]", " "), @"\s+", " ").Trim();
    }
    public static string NormalizeArtist(string value)
    {
        var ascii = new string(value.Normalize(NormalizationForm.FormKD).Where(c => c <= 127).ToArray()).ToLowerInvariant().Trim();
        return Regex.Replace(Regex.Replace(ascii, "^the\\s+", ""), "[^a-z0-9]+", "");
    }
    public static string MergeLinks(string? original, IEnumerable<SocialLink> additions)
    {
        var array = string.IsNullOrWhiteSpace(original) ? new JsonArray() : JsonNode.Parse(original) as JsonArray
            ?? throw new InvalidOperationException("Existing social links are malformed.");
        var youtubeMusicCount = array.Count(n => IsYouTubeMusicLink(
            n?["platform"]?.GetValue<string>(), n?["url"]?.GetValue<string>()));
        foreach (var link in additions)
        {
            if (array.Any(n => n?["url"]?.GetValue<string>() == link.Url)) continue;
            var isYouTubeMusic = IsYouTubeMusicLink(link.Platform, link.Url);
            if (isYouTubeMusic && youtubeMusicCount >= 3) continue;
            array.Add(JsonSerializer.SerializeToNode(link));
            if (isYouTubeMusic) youtubeMusicCount++;
        }
        return array.ToJsonString();
    }
    private static bool IsYouTubeMusicLink(string? platform, string? url) =>
        string.Equals(platform, "youtube_music", StringComparison.OrdinalIgnoreCase) ||
        (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase));
    public static void Validate(JsonElement output)
    {
        foreach (var field in new[] { "event_title", "venue", "city", "description" })
            if (!output.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"Research returned invalid {field}. No changes were made.");
        if (!output.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Research did not return a valid performer list.");
        foreach (var artist in artists.EnumerateArray())
            if (artist.ValueKind != JsonValueKind.Object ||
                !artist.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString()) || NormalizeArtist(name.GetString()!).Length == 0)
                throw new InvalidOperationException("Research returned an invalid performer. No changes were made.");
    }
}
