namespace Concert_Viewer.Data.Entities;

public sealed class CanonicalEvent
{
    public int Id { get; set; }

    public string? CanonicalKey { get; set; }

    public string? EventTitle { get; set; }

    public string? NormalizedTitle { get; set; }

    public string EventDate { get; set; } = string.Empty;

    public string? EventStartTime { get; set; }

    public string? EventEndTime { get; set; }

    public string? Venue { get; set; }

    public string? NormalizedVenue { get; set; }

    public string? City { get; set; }

    public string? NormalizedCity { get; set; }

    public string? Timezone { get; set; }

    public string? DiceUrl { get; set; }

    public string? SongkickUrl { get; set; }

    public string? BandsintownUrl { get; set; }

    public string? ImageUrl { get; set; }

    public string? Price { get; set; }

    public string? Category { get; set; }

    public string? Description { get; set; }

    public string Status { get; set; } = "active";

    public double? MatchConfidence { get; set; }

    public string? CreatedAt { get; set; }

    public string? UpdatedAt { get; set; }

    public List<EventArtist> EventArtists { get; set; } = [];
}
