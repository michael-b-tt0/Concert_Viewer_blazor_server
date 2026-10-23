using System.Text.RegularExpressions;
using YTMusicAPI.Abstraction;
using YTMusicAPI.Model;

namespace Concert_Viewer.Services;

public sealed class YouTubeMusicEnricher(ISearchClient search)
{
    public async Task<(string ChannelUrl, List<SocialLink> Links)> EnrichAsync(string name, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        var normalized = CorrectionWorker.NormalizeArtist(name);
        if (normalized.Length == 0) throw new InvalidOperationException("The artist name cannot be matched.");
        var artists = await search.SearchArtistsChannelsAsync(new QueryRequest { Query = name }, token);
        var channel = artists.Result
            .Where(a => CorrectionWorker.NormalizeArtist(a.Title ?? "") == normalized)
            .Where(a => Regex.IsMatch(a.Id ?? "", "\\AUC[A-Za-z0-9_-]+\\z"))
            .Select(a => a.Id).FirstOrDefault();
        if (channel is null) throw new InvalidOperationException("No YouTube Music artist channel matched the artist name.");

        var result = await search.SearchTracksAsync(new QueryRequest { Query = name }, token);
        var links = result.Result.Where(t => t.AuthorChannelId == channel)
            .Where(t => Regex.IsMatch(t.Id ?? "", "\\A[A-Za-z0-9_-]+\\z"))
            .DistinctBy(t => t.Id).Take(3)
            .Select(t => new SocialLink("youtube_music", $"https://music.youtube.com/watch?v={t.Id}", $"{name} - {t.Title}"))
            .ToList();
        if (links.Count == 0) throw new InvalidOperationException("No tracks matched the artist channel; existing links are preserved.");
        return ($"https://www.youtube.com/channel/{channel}", links);
    }
}
