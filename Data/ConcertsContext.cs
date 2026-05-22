using Concert_Viewer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Concert_Viewer.Data;

public sealed class ConcertsContext(DbContextOptions<ConcertsContext> options) : DbContext(options)
{
    public DbSet<Artist> Artists => Set<Artist>();

    public DbSet<CanonicalEvent> CanonicalEvents => Set<CanonicalEvent>();

    public DbSet<EventArtist> EventArtists => Set<EventArtist>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CanonicalEvent>(entity =>
        {
            entity.ToTable("canonical_events");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CanonicalKey).HasColumnName("canonical_key");
            entity.Property(e => e.EventTitle).HasColumnName("event_title");
            entity.Property(e => e.NormalizedTitle).HasColumnName("normalized_title");
            entity.Property(e => e.EventDate).HasColumnName("event_date");
            entity.Property(e => e.EventStartTime).HasColumnName("event_start_time");
            entity.Property(e => e.EventEndTime).HasColumnName("event_end_time");
            entity.Property(e => e.Venue).HasColumnName("venue");
            entity.Property(e => e.NormalizedVenue).HasColumnName("normalized_venue");
            entity.Property(e => e.City).HasColumnName("city");
            entity.Property(e => e.NormalizedCity).HasColumnName("normalized_city");
            entity.Property(e => e.Timezone).HasColumnName("timezone");
            entity.Property(e => e.DiceUrl).HasColumnName("dice_url");
            entity.Property(e => e.SongkickUrl).HasColumnName("songkick_url");
            entity.Property(e => e.BandsintownUrl).HasColumnName("bandsintown_url");
            entity.Property(e => e.ImageUrl).HasColumnName("image_url");
            entity.Property(e => e.Price).HasColumnName("price");
            entity.Property(e => e.Category).HasColumnName("category");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.MatchConfidence).HasColumnName("match_confidence");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");

            entity.HasMany(e => e.EventArtists)
                .WithOne(ea => ea.CanonicalEvent)
                .HasForeignKey(ea => ea.CanonicalEventId);
        });

        modelBuilder.Entity<Artist>(entity =>
        {
            entity.ToTable("artists");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.FormalName).HasColumnName("formal_name");
            entity.Property(e => e.NormalizedName).HasColumnName("normalized_name");
            entity.Property(e => e.BandsintownArtistUrl).HasColumnName("bandsintown_artist_url");
            entity.Property(e => e.LastFmPage).HasColumnName("lastfmpage");
            entity.Property(e => e.MusicBrainzId).HasColumnName("musicbrainzid");
            entity.Property(e => e.MusicBrainzPage).HasColumnName("musicbrainz_page");
            entity.Property(e => e.ArtistTags).HasColumnName("artist_tags");
            entity.Property(e => e.SocialLinks).HasColumnName("sociallinks");
            entity.Property(e => e.ImageUrl).HasColumnName("image_url");
            entity.Property(e => e.SpotifyUrl).HasColumnName("spotify_url");
            entity.Property(e => e.InstagramUrl).HasColumnName("instagram_url");
            entity.Property(e => e.YouTubeUrl).HasColumnName("youtube_url");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");

            entity.HasMany(e => e.EventArtists)
                .WithOne(ea => ea.Artist)
                .HasForeignKey(ea => ea.ArtistId);
        });

        modelBuilder.Entity<EventArtist>(entity =>
        {
            entity.ToTable("event_artists");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CanonicalEventId).HasColumnName("canonical_event_id");
            entity.Property(e => e.ArtistId).HasColumnName("artist_id");
            entity.Property(e => e.BillingPosition).HasColumnName("billing_position");
            entity.Property(e => e.Role).HasColumnName("role");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        });
    }
}
