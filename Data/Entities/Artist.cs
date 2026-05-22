namespace Concert_Viewer.Data.Entities;

public sealed class Artist
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? FormalName { get; set; }

    public string NormalizedName { get; set; } = string.Empty;

    public string? BandsintownArtistUrl { get; set; }

    public string? LastFmPage { get; set; }

    public string? MusicBrainzId { get; set; }

    public string? MusicBrainzPage { get; set; }

    public string? ArtistTags { get; set; }

    public string? SocialLinks { get; set; }

    public string? ImageUrl { get; set; }

    public string? SpotifyUrl { get; set; }

    public string? InstagramUrl { get; set; }

    public string? YouTubeUrl { get; set; }

    public string? CreatedAt { get; set; }

    public string? UpdatedAt { get; set; }

    public List<EventArtist> EventArtists { get; set; } = [];
}
