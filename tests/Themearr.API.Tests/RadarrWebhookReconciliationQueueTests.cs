using System.Text.Json;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public sealed class RadarrWebhookReconciliationQueueTests
{
    private static JsonElement Payload(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Tmdb_identity_matches_the_same_movie_across_instances()
    {
        using var temp = new TempDir();
        var db = new Database(Path.Combine(temp.Path, "test.db"));
        db.Init();
        var queue = new RadarrWebhookReconciliationQueue(db);
        queue.Enqueue(Payload("""{"eventType":"Download","movie":{"id":12,"tmdbId":603,"title":"The Matrix","year":1999,"path":"/movies/The Matrix"}}"""));

        var identity = Assert.Single(queue.Drain());

        Assert.True(identity.Matches(new MovieRecord("/other", "radarr", "ref", "Different local title",
            1999, "/other", "another-instance", "999", "4K", "603", "tt0133093")));
        Assert.False(identity.Matches(new MovieRecord("/other", "radarr", "ref", "The Matrix",
            1999, "/other", "another-instance", "999", "4K", "604", "tt0133093")));
    }

    [Fact]
    public void Duplicate_webhooks_for_one_movie_are_coalesced()
    {
        using var temp = new TempDir();
        var db = new Database(Path.Combine(temp.Path, "test.db"));
        db.Init();
        var queue = new RadarrWebhookReconciliationQueue(db);
        var payload = Payload("""{"eventType":"Download","movie":{"tmdbId":603}}""");

        queue.Enqueue(payload);
        queue.Enqueue(payload);
        queue.Enqueue(payload);

        Assert.Single(queue.Drain());
        Assert.Empty(queue.Drain());
    }

    [Fact]
    public void Missing_movie_identity_requests_a_safe_full_reconciliation()
    {
        using var temp = new TempDir();
        var db = new Database(Path.Combine(temp.Path, "test.db"));
        db.Init();
        var queue = new RadarrWebhookReconciliationQueue(db);
        queue.Enqueue(Payload("""{"eventType":"Download"}"""));

        var identity = Assert.Single(queue.Drain());

        Assert.True(identity.Matches(new MovieRecord("/any", "radarr", "ref", "Any", 2000, "/any")));
    }
}
