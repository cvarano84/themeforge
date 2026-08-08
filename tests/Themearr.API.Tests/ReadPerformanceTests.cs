using System.Diagnostics;
using System.Text.Json;
using Themearr.API.Data;
using Themearr.API.Services;
using Xunit.Abstractions;

namespace Themearr.API.Tests;

/// <summary>
/// Repeatable large-library read benchmark. The setup deliberately uses nonexistent
/// media folders: paginated local-state reads must still return them and must never stat
/// those paths. Timings exclude fixture creation and cover the same database methods used
/// by Dashboard, Movies, and Queue controllers.
/// </summary>
public sealed class ReadPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void Navigation_reads_stay_bounded_with_25000_movies_and_five_instances()
    {
        using var temp = new TempDir();
        var db = new Database(Path.Combine(temp.Path, "performance.db"));
        db.Init();

        const int movieCount = 25_000;
        var rows = Enumerable.Range(1, movieCount).Select(index =>
        {
            var instance = $"radarr-{index % 5}";
            return new MovieRecord(
                Path.Combine(temp.Path, "not-mounted", index.ToString()),
                "radarr", $"radarr:{instance}:{index}", $"Movie {index:D5}",
                1980 + index % 45, $"/movies/Movie {index:D5}", instance,
                index.ToString(), index % 2 == 0 ? "4K" : "1080p", index.ToString());
        }).ToList();
        db.UpsertMovies(rows);
        db.SetMovieStatuses(rows.Select((row, index) =>
            (MediaFolderId.For(row.Folder), index % 3 == 0 ? "downloaded" : "pending")));

        // Warm SQLite page cache and JIT before recording representative navigation calls.
        _ = db.GetMoviePage();
        _ = db.GetDashboardSummary();

        var dashboard = Measure(() => db.GetDashboardSummary());
        var activity = Measure(() => db.GetDashboardActivity());
        var firstPage = Measure(() => db.GetMoviePage(page: 1, pageSize: 50));
        var filtered = Measure(() => db.GetMoviePage(page: 1, pageSize: 50, status: "missing", quality: "4K"));
        var search = Measure(() => db.GetMoviePage(page: 1, pageSize: 50, search: "Movie 249"));
        var queue = Measure(() => db.GetMoviePage(page: 1, pageSize: 50, status: "outstanding", sort: "syncedAt", direction: "desc"));
        var movieSerialization = Measure(() => JsonSerializer.Serialize(firstPage.Value));
        var dashboardSerialization = Measure(() => JsonSerializer.Serialize(dashboard.Value));

        output.WriteLine($"Dashboard={dashboard.Elapsed.TotalMilliseconds:F1}ms");
        output.WriteLine($"DashboardActivity={activity.Elapsed.TotalMilliseconds:F1}ms");
        output.WriteLine($"MoviesFirstPage={firstPage.Elapsed.TotalMilliseconds:F1}ms");
        output.WriteLine($"MoviesFiltered={filtered.Elapsed.TotalMilliseconds:F1}ms");
        output.WriteLine($"MoviesSearch={search.Elapsed.TotalMilliseconds:F1}ms");
        output.WriteLine($"QueueFirstPage={queue.Elapsed.TotalMilliseconds:F1}ms");
        output.WriteLine($"MoviesSerialization={movieSerialization.Elapsed.TotalMilliseconds:F1}ms ({movieSerialization.Value.Length} chars)");
        output.WriteLine($"DashboardSerialization={dashboardSerialization.Elapsed.TotalMilliseconds:F1}ms ({dashboardSerialization.Value.Length} chars)");

        Assert.Equal(50, firstPage.Value.Items.Count);
        Assert.Equal(movieCount, firstPage.Value.Total);
        Assert.Equal(50, queue.Value.Items.Count);
        Assert.All(firstPage.Value.Items, item => Assert.StartsWith("Movie ", item["title"]?.ToString()));

        // Allow headroom for shared CI runners while enforcing the product's sub-second
        // target on the actual 25k-row data-access methods.
        Assert.True(dashboard.Elapsed < TimeSpan.FromSeconds(1), $"Dashboard took {dashboard.Elapsed}");
        Assert.True(activity.Elapsed < TimeSpan.FromSeconds(1), $"Dashboard activity took {activity.Elapsed}");
        Assert.True(firstPage.Elapsed < TimeSpan.FromSeconds(1), $"Movies took {firstPage.Elapsed}");
        Assert.True(filtered.Elapsed < TimeSpan.FromSeconds(1), $"Filtered movies took {filtered.Elapsed}");
        Assert.True(search.Elapsed < TimeSpan.FromSeconds(1), $"Search took {search.Elapsed}");
        Assert.True(queue.Elapsed < TimeSpan.FromSeconds(1), $"Queue took {queue.Elapsed}");
        Assert.True(movieSerialization.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(dashboardSerialization.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(1, 1_000)]
    [InlineData(3, 10_000)]
    public void Smaller_reference_datasets_meet_the_same_bounded_read_target(int instances, int movieCount)
    {
        using var temp = new TempDir();
        var db = new Database(Path.Combine(temp.Path, $"performance-{instances}-{movieCount}.db"));
        db.Init();
        db.UpsertMovies(Enumerable.Range(1, movieCount).Select(index => new MovieRecord(
            Path.Combine(temp.Path, "offline", index.ToString()), "radarr",
            $"radarr:r{index % instances}:{index}", $"Movie {index:D5}", 2000 + index % 25,
            $"/movies/{index}", $"r{index % instances}", index.ToString(), "1080p", index.ToString())));

        _ = db.GetMoviePage();
        var dashboard = Measure(() => db.GetDashboardSummary());
        var movies = Measure(() => db.GetMoviePage(pageSize: 50));
        var queue = Measure(() => db.GetMoviePage(pageSize: 50, status: "outstanding"));
        output.WriteLine($"{instances} Radarr/{movieCount} movies: dashboard={dashboard.Elapsed.TotalMilliseconds:F1}ms, movies={movies.Elapsed.TotalMilliseconds:F1}ms, queue={queue.Elapsed.TotalMilliseconds:F1}ms");

        Assert.True(dashboard.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(movies.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(queue.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Multi_instance_copies_are_grouped_after_pagination_without_loading_the_library()
    {
        using var temp = new TempDir();
        var db = new Database(Path.Combine(temp.Path, "duplicates.db"));
        db.Init();
        var rows = Enumerable.Range(1, 1_000).SelectMany(movie =>
            Enumerable.Range(1, 3).Select(instance => new MovieRecord(
                Path.Combine(temp.Path, "missing", $"{movie}-{instance}"), "radarr",
                $"radarr:r{instance}:{movie}", $"Movie {movie:D4}", 2000,
                $"/movies/{movie}", $"r{instance}", movie.ToString(), instance == 1 ? "4K" : "1080p",
                movie.ToString()))).ToList();
        db.UpsertMovies(rows);

        var page = db.GetMoviePage(pageSize: 25);

        Assert.Equal(1_000, page.Total);
        Assert.Equal(25, page.Items.Count);
        Assert.All(page.Items, item =>
        {
            Assert.Equal(3, item["locationCount"]);
            Assert.Equal(3, Assert.IsAssignableFrom<IReadOnlyCollection<Dictionary<string, object?>>>(item["locations"]).Count);
        });
    }

    [Fact]
    public void Queue_continuation_excludes_processed_items_when_live_rows_shift()
    {
        using var temp = new TempDir();
        var db = new Database(Path.Combine(temp.Path, "queue-continuation.db"));
        db.Init();
        db.UpsertMovies(Enumerable.Range(1, 100).Select(index => new MovieRecord(
            Path.Combine(temp.Path, "offline", index.ToString()), "radarr",
            $"radarr:r1:{index}", $"Movie {index:D3}", 2000,
            $"/movies/{index}", "r1", index.ToString(), "1080p", index.ToString())));

        var first = db.GetMoviePage(pageSize: 50, status: "outstanding", sort: "syncedAt", direction: "desc");
        var processed = first.Items.Take(45).Select(item => item["id"]!.ToString()!).ToArray();

        // Simulate rows changing underneath the queue while the user works through
        // the first batch. An offset-based second page would skip unseen records.
        db.SetMovieStatuses(processed.Take(20).Select(id => (id, "downloaded")));
        var continuation = db.GetMoviePage(pageSize: 50, status: "outstanding",
            sort: "syncedAt", direction: "desc", excludeIds: processed);

        Assert.Equal(55, continuation.Total);
        Assert.Equal(50, continuation.Items.Count);
        Assert.DoesNotContain(continuation.Items, item => processed.Contains(item["id"]?.ToString()));
    }

    private static (T Value, TimeSpan Elapsed) Measure<T>(Func<T> action)
    {
        var timer = Stopwatch.StartNew();
        var value = action();
        timer.Stop();
        return (value, timer.Elapsed);
    }
}
