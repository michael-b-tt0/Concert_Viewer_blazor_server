namespace Concert_Viewer.Data.Entities;

public sealed class EventArtist
{
    public int Id { get; set; }

    public int CanonicalEventId { get; set; }

    public int ArtistId { get; set; }

    public int? BillingPosition { get; set; }

    public string? Role { get; set; }

    public string? CreatedAt { get; set; }

    public CanonicalEvent? CanonicalEvent { get; set; }

    public Artist? Artist { get; set; }
}
