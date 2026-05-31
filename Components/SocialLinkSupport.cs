using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace Concert_Viewer.Components;

public enum SocialLinkProvider
{
    Unknown,
    Spotify,
    SoundCloud,
    YouTube,
    AppleMusic,
    Bandcamp,
    Deezer,
    Tidal,
    Discogs
}

public sealed record SocialLinkListItem(string Platform, string Url, SocialLinkProvider Provider);

public sealed record SocialEmbedListItem(
    string Platform,
    string Url,
    SocialLinkProvider Provider,
    string EmbedUrl);

public static class SocialLinkResolver
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> BandcampEmbedUrlCache = new();

    public static string NormalizePlatform(string? platform, string? url)
    {
        var cleanedPlatform = platform?.Trim();
        if (!string.IsNullOrWhiteSpace(cleanedPlatform) &&
            !string.Equals(cleanedPlatform, "social network", StringComparison.OrdinalIgnoreCase))
        {
            if (cleanedPlatform.Contains("youtube", StringComparison.OrdinalIgnoreCase))
            {
                return "YouTube";
            }

            return cleanedPlatform;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Contains("music.youtube.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
            {
                return "YouTube";
            }

            return uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        }

        return "link";
    }

    public static SocialLinkProvider DetectProvider(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return SocialLinkProvider.Unknown;
        }

        var host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);

        if (host.Contains("spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            return SocialLinkProvider.Spotify;
        }

        if (host.Contains("soundcloud.com", StringComparison.OrdinalIgnoreCase))
        {
            return SocialLinkProvider.SoundCloud;
        }

        if (host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("music.youtube.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            return SocialLinkProvider.YouTube;
        }

        if (host.Contains("music.apple.com", StringComparison.OrdinalIgnoreCase))
        {
            return SocialLinkProvider.AppleMusic;
        }

        if (host.Contains("bandcamp.com", StringComparison.OrdinalIgnoreCase))
        {
            return SocialLinkProvider.Bandcamp;
        }

        if (host.Contains("deezer.com", StringComparison.OrdinalIgnoreCase))
        {
            return SocialLinkProvider.Deezer;
        }

        if (host.Contains("tidal.com", StringComparison.OrdinalIgnoreCase))
        {
            return SocialLinkProvider.Tidal;
        }

        if (host.Contains("discogs.com", StringComparison.OrdinalIgnoreCase))
        {
            return SocialLinkProvider.Discogs;
        }

        return SocialLinkProvider.Unknown;
    }

    public static string GetDisplayLabel(SocialLinkListItem socialLink)
    {
        if (socialLink.Provider == SocialLinkProvider.Bandcamp &&
            Uri.TryCreate(socialLink.Url, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (segments.Length >= 2)
            {
                if (string.Equals(segments[0], "track", StringComparison.OrdinalIgnoreCase))
                {
                    return "Bandcamp track";
                }

                if (string.Equals(segments[0], "album", StringComparison.OrdinalIgnoreCase))
                {
                    return "Bandcamp album";
                }
            }

            return "Bandcamp";
        }

        return socialLink.Platform;
    }

    public static List<SocialEmbedListItem> GetEmbeds(IEnumerable<SocialLinkListItem> socialLinks)
    {
        return socialLinks
            .Select(TryCreateEmbed)
            .Where(embed => embed is not null)
            .Cast<SocialEmbedListItem>()
            .Distinct()
            .ToList();
    }

    public static List<SocialLinkListItem> GetNonEmbeddableLinks(IEnumerable<SocialLinkListItem> socialLinks)
    {
        return socialLinks
            .Where(link => TryCreateEmbed(link) is null)
            .ToList();
    }

    public static SocialEmbedListItem? TryCreateEmbed(SocialLinkListItem link)
    {
        var embedUrl = link.Provider switch
        {
            SocialLinkProvider.Spotify => TryCreateSpotifyEmbedUrl(link.Url),
            SocialLinkProvider.SoundCloud => TryCreateSoundCloudEmbedUrl(link.Url),
            SocialLinkProvider.YouTube => TryCreateYouTubeEmbedUrl(link.Url),
            SocialLinkProvider.AppleMusic => TryCreateAppleMusicEmbedUrl(link.Url),
            SocialLinkProvider.Bandcamp => TryCreateBandcampEmbedUrl(link.Url),
            SocialLinkProvider.Deezer => TryCreateDeezerEmbedUrl(link.Url),
            _ => null
        };

        return embedUrl is null
            ? null
            : new SocialEmbedListItem(link.Platform, link.Url, link.Provider, embedUrl);
    }

    private static string? TryCreateSpotifyEmbedUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!uri.Host.Contains("spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length < 2)
        {
            return null;
        }

        var resourceType = segments[0];
        var resourceId = segments[1];
        var supportedResourceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "artist",
            "album",
            "track",
            "playlist",
            "episode",
            "show"
        };

        if (!supportedResourceTypes.Contains(resourceType) || string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }

        return $"https://open.spotify.com/embed/{resourceType}/{resourceId}";
    }

    private static string? TryCreateSoundCloudEmbedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.Contains("soundcloud.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var encodedUrl = Uri.EscapeDataString(url);
        return $"https://w.soundcloud.com/player/?url={encodedUrl}&color=%230b6b43&auto_play=false&hide_related=false&show_comments=false&show_user=true&show_reposts=false&show_teaser=true&visual=false";
    }

    private static string? TryCreateYouTubeEmbedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (!uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) &&
             !uri.Host.Contains("music.youtube.com", StringComparison.OrdinalIgnoreCase) &&
             !uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var videoId = GetYouTubeVideoId(uri);
        if (!string.IsNullOrWhiteSpace(videoId))
        {
            return $"https://www.youtube.com/embed/{videoId}";
        }

        var playlistId = GetYouTubePlaylistId(uri);
        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            return $"https://www.youtube.com/embed/videoseries?list={playlistId}";
        }

        return null;
    }

    public static string? NormalizeUrlForDisplay(string? url, SocialLinkProvider provider)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (provider != SocialLinkProvider.YouTube)
        {
            return url.Trim();
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (!uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) &&
             !uri.Host.Contains("music.youtube.com", StringComparison.OrdinalIgnoreCase) &&
             !uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var videoId = GetYouTubeVideoId(uri);
        if (!string.IsNullOrWhiteSpace(videoId))
        {
            return $"https://www.youtube.com/watch?v={videoId}";
        }

        var playlistId = GetYouTubePlaylistId(uri);
        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            return $"https://www.youtube.com/playlist?list={playlistId}";
        }

        if (HasInvalidYouTubeReference(uri))
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttps,
            Host = "www.youtube.com",
            Port = -1
        };

        return builder.Uri.ToString();
    }

    private static bool HasInvalidYouTubeReference(Uri uri)
    {
        var videoId = GetQueryParameter(uri, "v");
        if (videoId is not null &&
            (string.IsNullOrWhiteSpace(videoId) || string.Equals(videoId, "none", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var playlistId = GetQueryParameter(uri, "list");
        if (playlistId is not null &&
            (string.IsNullOrWhiteSpace(playlistId) || string.Equals(playlistId, "none", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length >= 2 && segments[0] is "embed" or "shorts")
        {
            return string.IsNullOrWhiteSpace(segments[1]) ||
                   string.Equals(segments[1], "none", StringComparison.OrdinalIgnoreCase);
        }

        if (segments.Length >= 1 && uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(segments[0]) ||
                   string.Equals(segments[0], "none", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string? GetYouTubeVideoId(Uri uri)
    {
        var videoId = GetQueryParameter(uri, "v");
        if (!string.IsNullOrWhiteSpace(videoId) &&
            !string.Equals(videoId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return videoId;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length >= 2 && segments[0] is "embed" or "shorts")
        {
            var candidateId = segments[1];
            if (!string.IsNullOrWhiteSpace(candidateId) &&
                !string.Equals(candidateId, "none", StringComparison.OrdinalIgnoreCase))
            {
                return candidateId;
            }
        }

        if (segments.Length >= 1 && uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            var candidateId = segments[0];
            if (!string.IsNullOrWhiteSpace(candidateId) &&
                !string.Equals(candidateId, "none", StringComparison.OrdinalIgnoreCase))
            {
                return candidateId;
            }
        }

        return null;
    }

    private static string? GetYouTubePlaylistId(Uri uri)
    {
        var playlistId = GetQueryParameter(uri, "list");
        if (string.IsNullOrWhiteSpace(playlistId) ||
            string.Equals(playlistId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return playlistId;
    }

    private static string? GetQueryParameter(Uri uri, string key)
    {
        var query = uri.Query;
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var segments = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            var parts = segment.Split('=', 2);
            var name = WebUtility.UrlDecode(parts[0]);
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (parts.Length < 2)
            {
                return string.Empty;
            }

            return WebUtility.UrlDecode(parts[1]);
        }

        return null;
    }

    private static string? TryCreateBandcampEmbedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!uri.Host.Contains("bandcamp.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (uri.AbsolutePath.Contains("/EmbeddedPlayer/", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        if (!IsBandcampEmbeddablePageUrl(uri))
        {
            return null;
        }

        // Plain Bandcamp album/track page URLs are the right kind of page for embedding,
        // but they still need Bandcamp's EmbeddedPlayer URL format before we can render
        // a working iframe.
        return null;
    }

    private static bool IsBandcampEmbeddablePageUrl(Uri uri)
    {
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length < 2)
        {
            return false;
        }

        return segments[0] is "album" or "track";
    }

    private static string? TryCreateDeezerEmbedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.Contains("deezer.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length < 2)
        {
            return null;
        }

        var supportedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "artist",
            "album",
            "track",
            "playlist",
            "podcast",
            "episode"
        };

        var contentTypeIndex = 0;
        if (segments.Length >= 3 && segments[0].Length == 2)
        {
            contentTypeIndex = 1;
        }

        var contentType = segments[contentTypeIndex];
        var contentId = segments[contentTypeIndex + 1];

        if (!supportedTypes.Contains(contentType) || string.IsNullOrWhiteSpace(contentId))
        {
            return null;
        }

        // Deezer artist widgets need the top_tracks variant to render a playable widget.
        var suffix = string.Equals(contentType, "artist", StringComparison.OrdinalIgnoreCase)
            ? "/top_tracks"
            : string.Empty;

        return $"https://widget.deezer.com/widget/auto/{contentType}/{contentId}{suffix}";
    }

    private static string? TryCreateAppleMusicEmbedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.Contains("music.apple.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length < 2)
        {
            return null;
        }

        var contentType = segments[1];
        var supportedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "album",
            "song",
            "playlist"
        };

        if (!supportedTypes.Contains(contentType))
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttps,
            Host = "embed.music.apple.com",
            Port = -1
        };

        return builder.Uri.ToString();
    }

    public static Task<string?> FetchBandcampEmbedUrlAsync(string url, HttpClient httpClient)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.Contains("bandcamp.com", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<string?>(null);
        }

        if (uri.AbsolutePath.Contains("/EmbeddedPlayer/", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<string?>(url);
        }

        if (!IsBandcampEmbeddablePageUrl(uri))
        {
            return Task.FromResult<string?>(null);
        }

        var cacheKey = uri.GetLeftPart(UriPartial.Path).ToLowerInvariant();
        var cachedLookup = BandcampEmbedUrlCache.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<string?>>(
                () => FetchBandcampEmbedUrlCoreAsync(uri, httpClient),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return cachedLookup.Value;
    }

    private static async Task<string?> FetchBandcampEmbedUrlCoreAsync(Uri uri, HttpClient httpClient)
    {
        try
        {
            var html = await httpClient.GetStringAsync(uri);
            var match = Regex.Match(
                html,
                @"<meta\b(?=[^>]*(?:name|property)=[""']twitter:player[""'])(?=[^>]*content=[""'](?<url>[^""']+)[""'])[^>]*>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (!match.Success)
            {
                return null;
            }

            var embedUrl = WebUtility.HtmlDecode(match.Groups["url"].Value);
            if (!Uri.TryCreate(embedUrl, UriKind.Absolute, out var embedUri) ||
                !embedUri.Host.Contains("bandcamp.com", StringComparison.OrdinalIgnoreCase) ||
                !embedUri.AbsolutePath.Contains("/EmbeddedPlayer/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return embedUrl.Replace("size=large", "size=small", StringComparison.OrdinalIgnoreCase);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }
}
