namespace Concert_Viewer.Components;

public enum SocialLinkProvider
{
    Unknown,
    Spotify,
    SoundCloud,
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
    public static string NormalizePlatform(string? platform, string? url)
    {
        var cleanedPlatform = platform?.Trim();
        if (!string.IsNullOrWhiteSpace(cleanedPlatform) &&
            !string.Equals(cleanedPlatform, "social network", StringComparison.OrdinalIgnoreCase))
        {
            return cleanedPlatform;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
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

    private static SocialEmbedListItem? TryCreateEmbed(SocialLinkListItem link)
    {
        var embedUrl = link.Provider switch
        {
            SocialLinkProvider.Spotify => TryCreateSpotifyEmbedUrl(link.Url),
            SocialLinkProvider.SoundCloud => TryCreateSoundCloudEmbedUrl(link.Url),
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

        return  $"https://widget.deezer.com/widget/auto/{contentType}/{contentId}";
    }
}
