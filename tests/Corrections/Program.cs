using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Concert_Viewer.Data;
using Concert_Viewer.Data.Entities;
using Concert_Viewer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Concert_Viewer.Components;
using System.Reflection;
using YTMusicAPI.Abstraction;
using YTMusicAPI.Model;
using MusicArtist = YTMusicAPI.Model.Domain.Artist;
using MusicAlbum = YTMusicAPI.Model.Domain.Album;
using MusicTrack = YTMusicAPI.Model.Domain.Track;

var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"));
void Check(bool value, string message) { if (!value) throw new Exception(message); }
var gate = new OperationGate();
using (gate.Enter("scraper"))
{
    try { gate.Enter("research"); throw new Exception("Concurrent operation was allowed"); }
    catch (InvalidOperationException) { }
}
Check(!gate.IsBusy, "Lease was not released");
Check(CorrectionWorker.NormalizeArtist("The Déjà Vu!") == "dejavu", "Artist normalization mismatch");
var merged = CorrectionWorker.MergeLinks("""[{"platform":"spotify","url":"https://open.spotify.com/a","label":"keep"}]""",
    [new("youtube_music", "https://music.youtube.com/watch?v=abc", "song")]);
Check(merged.Contains("spotify") && merged.Contains("keep") && merged.Contains("abc"), "Link merging lost data");
var capped = CorrectionWorker.MergeLinks(merged,
    [new("youtube_music", "https://music.youtube.com/watch?v=abc", "duplicate"),
     new("youtube_music", "https://music.youtube.com/watch?v=second", "Second"),
     new("youtube_music", "https://music.youtube.com/watch?v=third", "Third"),
     new("youtube_music", "https://music.youtube.com/watch?v=fourth", "Fourth"),
     new("bandcamp", "https://artist.bandcamp.com", "Bandcamp")]);
var cappedLinks = JsonSerializer.Deserialize<List<SocialLink>>(capped)!;
Check(cappedLinks.Count(l => l.Platform == "youtube_music") == 3 &&
    !cappedLinks.Any(l => l.Url.EndsWith("fourth")), "Merging exceeded three YouTube Music links");
Check(cappedLinks.Any(l => l.Platform == "spotify" && l.Label == "keep") &&
    cappedLinks.Any(l => l.Platform == "bandcamp"), "YouTube Music cap affected other social links");
Check(CorrectionWorker.MergeLinks(capped, [new("youtube_music", "https://music.youtube.com/watch?v=fifth")]) == capped,
    "Repeated enrichment added links past the cap");
var legacyLinks = JsonSerializer.Serialize(Enumerable.Range(1, 4).Select(i =>
    new SocialLink("YouTube", $"https://music.youtube.com/watch?v=legacy{i}", $"Keep {i}")));
Check(CorrectionWorker.MergeLinks(legacyLinks, [new("youtube_music", "https://music.youtube.com/watch?v=new")]) == legacyLinks,
    "Existing music URLs were not counted or links already above the cap were removed");
Console.WriteLine("PASS: YouTube Music merging caps additions at three across repeated runs, counts existing music URLs, and preserves other social links and existing excess links.");

var search = new FakeMusicSearch();
var youtube = new YouTubeMusicEnricher(search);
using (var cancellation = new CancellationTokenSource())
{
    search.Artists = [new() { Id = "UCunrelated", Title = "Other artist" },
        new() { Id = "UCartist", Title = "NEW ARTIST!" }, new() { Id = "UCartist", Title = "New artist" }];
    search.Tracks = [new() { Id = "other", AuthorChannelId = "UCunrelated", Title = "Wrong artist" },
        new() { Id = "bad&id", AuthorChannelId = "UCartist" },
        new() { Id = "bad\n", AuthorChannelId = "UCartist" },
        new() { Id = "first", AuthorChannelId = "UCartist", Title = "First" },
        new() { Id = "first", AuthorChannelId = "UCartist", Title = "Duplicate" },
        new() { Id = "second", AuthorChannelId = "UCartist", Title = "Second" },
        new() { Id = "third", AuthorChannelId = "UCartist", Title = "Third" },
        new() { Id = "fourth", AuthorChannelId = "UCartist", Title = "Fourth" }];
    var enriched = await youtube.EnrichAsync(" New artist ", cancellation.Token);
    Check(enriched.ChannelUrl == "https://www.youtube.com/channel/UCartist", "Wrong artist channel selected");
    Check(enriched.Links.Select(l => l.Url).SequenceEqual(new[] {
        "https://music.youtube.com/watch?v=first", "https://music.youtube.com/watch?v=second", "https://music.youtube.com/watch?v=third"
    }), "Track filtering, ordering, deduplication, or limit failed");
    Check(enriched.Links.All(l => l.Platform == "youtube_music") && enriched.Links[0].Label == "New artist - First", "Track metadata mismatch");
    Check(search.Requests.All(r => r.Query == "New artist" && r.Token == cancellation.Token), "Search did not receive artist name or cancellation token");

    search.Artists = [new() { Id = "UCother", Title = "Other artist" }, new() { Id = "UCbad/id", Title = "New artist" }];
    await ExpectEnrichmentFailure("No YouTube Music artist channel");
    search.Artists = [new() { Id = "UCartist", Title = "New artist" }, new() { Id = "UCanother", Title = "New artist" }];
    search.Tracks = [new() { Id = "another", AuthorChannelId = "UCanother", Title = "Other channel song" }, .. search.Tracks];
    var multipleMatches = await youtube.EnrichAsync("New artist", default);
    Check(multipleMatches.ChannelUrl == "https://www.youtube.com/channel/UCartist" &&
        multipleMatches.Links.Select(l => l.Url).SequenceEqual(enriched.Links.Select(l => l.Url)),
        "Multiple artist matches should use the first matching channel and only its tracks");
    search.Artists = [new() { Id = "UCartist", Title = "New artist" }];
    search.Tracks = [new() { Id = "wrong", AuthorChannelId = "UCother" }];
    await ExpectEnrichmentFailure("No tracks matched");
    search.Tracks = [new() { Id = "only", AuthorChannelId = "UCartist", Title = "Only song" }];
    Check((await youtube.EnrichAsync("New artist", default)).Links.Count == 1, "Fewer than three songs should be accepted");
    cancellation.Cancel();
    try { await youtube.EnrichAsync("New artist", cancellation.Token); throw new Exception("Cancellation was ignored"); }
    catch (OperationCanceledException) { }
}
Console.WriteLine("PASS: name-only artist lookup, first matching channel selection, missing artists, track filtering/order/limit, fewer tracks, and cancellation.");

async Task ExpectEnrichmentFailure(string message)
{
    try { await youtube.EnrichAsync("New artist", default); throw new Exception("Expected enrichment failure"); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains(message), "Unexpected enrichment failure"); }
}

await using var connection = new SqliteConnection("Data Source=:memory:");
await connection.OpenAsync();
var factory = new TestFactory(new DbContextOptionsBuilder<ConcertsContext>().UseSqlite(connection).Options);
await using (var db = factory.CreateDbContext())
{
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX artist_key ON artists(normalized_name)");
    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX event_artist_key ON event_artists(canonical_event_id, artist_id)");
    var artist = new Artist { Name = "Existing", NormalizedName = "existing", SocialLinks = "[]" };
    db.CanonicalEvents.Add(new CanonicalEvent { Id = 1, EventTitle = "Original", EventDate = "2026-10-30", Venue = "Keep venue", CanonicalKey = "stable-import-key",
        EventArtists = [new EventArtist { Artist = artist }] });
    db.CanonicalEvents.Add(new CanonicalEvent { Id = 2, EventTitle = "Shared", EventDate = "2026-10-30",
        EventArtists = [new EventArtist { Artist = artist }] });
    await db.SaveChangesAsync();
}
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
    ["Tavily:ApiKey"] = "test-key", ["Tavily:PollSeconds"] = "1"
}).Build();
var handler = new ResearchHandler();
var rejectedHandler = new RejectedResearchHandler();
using (var rejectedHttp = new HttpClient(rejectedHandler) { BaseAddress = new Uri("https://api.tavily.com/") })
{
    var rejectedClient = new TavilyResearchClient(rejectedHttp, configuration);
    try
    {
        await rejectedClient.ResearchAsync(new CanonicalEvent { EventTitle = "Example", EventDate = "2026-10-30" }, default);
        throw new Exception("Expected the API rejection to be reported");
    }
    catch (HttpRequestException ex)
    {
        Check(ex.StatusCode == HttpStatusCode.BadRequest, "API status was lost");
        Check(ex.Message.Contains("submission") && ex.Message.Contains("Invalid output_schema"), "API rejection detail was lost");
        Check(!ex.Message.Contains("test-key") && ex.Message.Contains("[redacted]"), "API key leaked in error detail");
        Check(rejectedHandler.Calls == 1, "Rejected submission was retried");
    }
}
Console.WriteLine("PASS: Tavily 400 includes response detail and submission stage, preserves status, redacts credentials, and is not retried.");
using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.tavily.com/") };
using var worker = new CorrectionWorker(gate, factory, new TavilyResearchClient(http, configuration),
    youtube, configuration, NullLogger<CorrectionWorker>.Instance);
await worker.StartAsync(default);
try
{
    try { worker.Start(1, new ClaimsPrincipal()); throw new Exception("Anonymous research allowed"); }
    catch (UnauthorizedAccessException) { }
    worker.Start(1, user);
    try { worker.Start(2, user); throw new Exception("Concurrent job allowed"); } catch (InvalidOperationException) { }
    await Ready();
    Check(worker.DiagnosticsAfter(0).Any(entry => entry.Message.Contains("in_progress")), "In-progress status was not polled through to completion");
    Check(gate.IsBusy, "Review did not retain gate");
    Check(worker.Current!.After!.Venue == "Keep venue", "Blank result overwrote venue");
    Check(worker.Current.After.Artists.Single().YouTubeUrl == "https://www.youtube.com/channel/UCartist" &&
        worker.Current.After.Artists.Single().SocialLinks!.Contains("watch?v=only"), "Name-only research did not enrich proposed artist links");
    Check(worker.Current.Warnings!.Count == 0, "Successful enrichment produced a warning");
    await worker.DecideAsync(worker.Current.Id, false, user);
    await using (var db = factory.CreateDbContext()) Check((await db.CanonicalEvents.FindAsync(1))!.EventTitle == "Original", "Reject wrote data");
    Check(!gate.IsBusy, "Reject retained gate");

    worker.Start(1, user); await Ready();
    var proposal = worker.Current!;
    await worker.DecideAsync(proposal.Id, true, user);
    await using (var db = factory.CreateDbContext())
    {
        var item = await db.CanonicalEvents.Include(e => e.EventArtists).ThenInclude(a => a.Artist).SingleAsync(e => e.Id == 1);
        Check(item.EventTitle == "Corrected" && item.EventDate == "2026-10-30" && item.CanonicalKey == "stable-import-key", "Incorrect event update");
        Check(item.EventArtists.Single().Artist!.Name == "New artist", "Lineup replacement failed");
        Check(item.EventArtists.Single().Artist!.YouTubeUrl == "https://www.youtube.com/channel/UCartist" &&
            item.EventArtists.Single().Artist!.SocialLinks!.Contains("watch?v=only"), "Accepted enrichment was not saved");
        Check(await db.Artists.AnyAsync(a => a.Name == "Existing") && await db.EventArtists.AnyAsync(a => a.CanonicalEventId == 2), "Shared artist was deleted");
    }
    try { await worker.DecideAsync(proposal.Id, true, user); throw new Exception("Double acceptance allowed"); } catch (InvalidOperationException) { }

    // Trigger a failure after event updates and association deletions to prove atomic rollback.
    worker.Start(1, user); await Ready();
    await using (var db = factory.CreateDbContext())
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_artist BEFORE UPDATE ON artists BEGIN SELECT RAISE(ABORT, 'test failure'); END");
    try { await worker.DecideAsync(worker.Current!.Id, true, user); throw new Exception("Expected save failure"); } catch (DbUpdateException) { }
    Check(gate.IsBusy && worker.Current!.Status == "Ready for review", "Save failure lost proposal or gate");
    await using (var db = factory.CreateDbContext())
    {
        Check(await db.EventArtists.CountAsync(a => a.CanonicalEventId == 1) == 1, "Rollback lost associations");
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_artist");
    }
    await worker.DecideAsync(worker.Current!.Id, false, user);

    search.Failure = new HttpRequestException("Simulated lookup failure");
    worker.Start(1, user); await Ready();
    var failedArtist = worker.Current!.After!.Artists.Single();
    var originalArtist = worker.Current.Before!.Artists.Single();
    Check(failedArtist.SocialLinks == originalArtist.SocialLinks && failedArtist.YouTubeUrl == originalArtist.YouTubeUrl,
        "Failed enrichment changed existing artist links");
    Check(worker.Current.Warnings!.Count == 1, "Failed enrichment did not show a warning");
    await worker.DecideAsync(worker.Current.Id, false, user);
    search.Failure = null;

    handler.ArtistNames = ["New artist", "Missing artist", "The New artist"];
    worker.Start(1, user); await Ready();
    Check(worker.Current!.After!.Artists.Count == 2 && worker.Current.Warnings!.Count == 1,
        "Missing artist match stopped the lineup or duplicate artist names were enriched twice");
    Check(worker.Current.After.Artists[0].YouTubeUrl is not null && worker.Current.After.Artists[1].YouTubeUrl is null,
        "Per-artist name lookup results were mixed");
    await worker.DecideAsync(worker.Current.Id, false, user);
    Console.WriteLine("PASS: names-only research enriches every unique performer; failures preserve links and allow review of the remaining lineup.");

    handler.EmptyArtists = true;
    worker.Start(1, user); await Ready();
    Check(worker.Current!.After!.Artists.Count == 0, "Empty lineup was not proposed");
    await worker.DecideAsync(worker.Current.Id, true, user);
    await using (var db = factory.CreateDbContext()) Check(!await db.EventArtists.AnyAsync(a => a.CanonicalEventId == 1), "Empty lineup was not applied");

    handler.Invalid = true;
    worker.Start(1, user); await Ready("Failed");
    Check(!gate.IsBusy && worker.Current!.After is null, "Invalid result retained gate or offered acceptance");
    // Exercise the actual component interop call: a covariant array passed to params
    // object[] used to become separate JS arguments, making entries non-iterable.
    var browser = new DiagnosticBrowser();
    var review = new CorrectionReview();
    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
    typeof(CorrectionReview).GetProperty("Corrections", flags)!.SetValue(review, worker);
    typeof(CorrectionReview).GetProperty("JS", flags)!.SetValue(review, browser);
    var afterRender = typeof(CorrectionReview).GetMethod("OnAfterRenderAsync", flags)!;
    await (Task)afterRender.Invoke(review, [false])!;
    Check(browser.LogCalls == 1 && browser.Entries.Length > 0, "Diagnostic batch did not reach browser");
    Check(browser.Entries.Any(e => e.Level == "error"), "Failure diagnostics missing");
    await (Task)afterRender.Invoke(review, [false])!;
    Check(browser.LogCalls == 1, "Diagnostic batch was resent without new entries");
    await review.DisposeAsync();
    Console.WriteLine("PASS: component forwards diagnostics as one JavaScript array argument and does not resend existing entries.");
    Console.WriteLine("PASS: authentication, global exclusion, review lease, reject, atomic accept/rollback, fixed date/import key, performer replacement, shared artists, empty lineup, invalid response, link merge.");
}
finally { await worker.StopAsync(default); }

async Task Ready(string status = "Ready for review")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (worker.Current?.Status != status)
    {
        if (worker.Current?.Status == "Failed") throw new Exception(worker.Current.Error);
        await Task.Delay(20, timeout.Token);
    }
}
sealed class TestFactory(DbContextOptions<ConcertsContext> options) : IDbContextFactory<ConcertsContext>
{
    public ConcertsContext CreateDbContext() => new(options);
}
sealed class DiagnosticBrowser : IJSRuntime, IJSObjectReference
{
    public int LogCalls { get; private set; }
    public CorrectionDiagnostic[] Entries { get; private set; } = [];
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args)
    {
        if (identifier == "import") return ValueTask.FromResult((TValue)(object)this);
        if (identifier == "logDiagnostics")
        {
            if (args is not [CorrectionDiagnostic[] entries])
                throw new Exception("logDiagnostics expected one array argument; received expanded arguments.");
            Entries = entries;
            LogCalls++;
        }
        return ValueTask.FromResult(default(TValue)!);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
sealed class FakeMusicSearch : ISearchClient
{
    public IReadOnlyList<MusicArtist> Artists { get; set; } = [];
    public IReadOnlyList<MusicTrack> Tracks { get; set; } = [];
    public Exception? Failure { get; set; }
    public List<(string Query, CancellationToken Token)> Requests { get; } = [];
    public Task<SearchingResult<MusicArtist>> SearchArtistsChannelsAsync(QueryRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Requests.Add((request.Query, token));
        if (Failure is not null) throw Failure;
        return Task.FromResult(new SearchingResult<MusicArtist>(Artists, "", ""));
    }
    public Task<SearchingResult<MusicTrack>> SearchTracksAsync(QueryRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Requests.Add((request.Query, token));
        if (Failure is not null) throw Failure;
        return Task.FromResult(new SearchingResult<MusicTrack>(Tracks, "", ""));
    }
    public Task<SearchingResult<MusicAlbum>> SearchAlbumsAsync(QueryRequest request, CancellationToken token) =>
        throw new Exception("Unexpected album search");
}
sealed class ResearchHandler : HttpMessageHandler
{
    private bool inProgress;
    public bool EmptyArtists { get; set; }
    public bool Invalid { get; set; }
    public string[] ArtistNames { get; set; } = ["New artist"];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method == HttpMethod.Post) inProgress = true;
        else if (inProgress)
        {
            inProgress = false;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) {
                Content = JsonContent.Create(new { request_id = "test-request", status = "in_progress" })
            });
        }
        object response = request.Method == HttpMethod.Post ? new { request_id = "test-request" } : (object)new {
            status = "completed", content = Invalid ? "{}" : JsonSerializer.Serialize(new {
                event_title = "Corrected", venue = "", city = "London", description = "Researched",
                artists = (EmptyArtists ? Array.Empty<string>() : ArtistNames).Select(name => new { name }).ToArray(),
                sources = new[] { "https://example.com/event", "javascript:alert(1)" }
            })
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) });
    }
}
sealed class RejectedResearchHandler : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Calls++;
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var root = payload.RootElement;
        var schema = root.GetProperty("output_schema");
        if (!schema.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[] { "properties", "required" }))
            throw new Exception("Tavily output_schema accepts only properties and required at the root");
        if (schema.GetProperty("properties").GetProperty("artists").GetProperty("items").GetProperty("type").GetString() != "object")
            throw new Exception("Nested artist object type must remain in the schema");
        var artistSchema = schema.GetProperty("properties").GetProperty("artists").GetProperty("items");
        if (!artistSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "name" }) ||
            !artistSchema.GetProperty("required").EnumerateArray().Select(p => p.GetString()).SequenceEqual(new[] { "name" }) ||
            root.GetProperty("input").GetString()!.Contains("youtube.com"))
            throw new Exception("Tavily should research artist names without music page URLs");
        CheckDescriptions(schema);
        if (root.GetProperty("model").GetString() != "mini" ||
            !root.GetProperty("output_schema").GetProperty("properties").TryGetProperty("artists", out _))
            throw new Exception("Unexpected serialized Tavily request");
        return new HttpResponseMessage(HttpStatusCode.BadRequest) {
            Content = JsonContent.Create(new { detail = new { error = "Invalid output_schema (test-key)" } })
        };
    }

    private static void CheckDescriptions(JsonElement schema)
    {
        if (schema.TryGetProperty("properties", out var properties))
            foreach (var property in properties.EnumerateObject())
            {
                if (!property.Value.TryGetProperty("description", out var description) ||
                    description.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(description.GetString()))
                    throw new Exception($"Tavily schema property '{property.Name}' requires a description");
                CheckDescriptions(property.Value);
            }
        if (schema.TryGetProperty("items", out var items)) CheckDescriptions(items);
    }
}
