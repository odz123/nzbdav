using System.Net;
using Microsoft.Extensions.Caching.Memory;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class RadarrClient(string host, string apiKey) : ArrClient(host, apiKey)
{
    // PERF FIX #16: Replace unbounded static dictionary with size-limited MemoryCache
    // to prevent memory leaks in long-running instances
    private static readonly MemoryCache SymlinkOrStrmToMovieIdCache = new(new MemoryCacheOptions
    {
        SizeLimit = 2000, // Limit to 2000 movie files
        ExpirationScanFrequency = TimeSpan.FromHours(1)
    });

    public Task<RadarrMovie> GetMovieAsync(int id) =>
        Get<RadarrMovie>($"/movie/{id}");

    public Task<List<RadarrMovie>> GetMoviesAsync() =>
        Get<List<RadarrMovie>>($"/movie");

    public Task<RadarrQueue> GetRadarrQueueAsync() =>
        Get<RadarrQueue>($"/queue?protocol=usenet&pageSize=5000");

    public Task<HttpStatusCode> DeleteMovieFile(int id) =>
        Delete($"/moviefile/{id}");

    public Task<ArrCommand> SearchMovieAsync(int id) =>
        CommandAsync(new { name = "MoviesSearch", movieIds = new List<int> { id } });


    public override async Task<bool> RemoveAndSearch(string symlinkOrStrmPath)
    {
        var mediaIds = await GetMediaIds(symlinkOrStrmPath);
        if (mediaIds == null) return false;

        if (await DeleteMovieFile(mediaIds.Value.movieFileId) != HttpStatusCode.OK)
            throw new Exception($"Failed to delete movie file `{symlinkOrStrmPath}` from radarr instance `{Host}`.");

        await SearchMovieAsync(mediaIds.Value.movieId);
        return true;
    }

    private async Task<(int movieFileId, int movieId)?> GetMediaIds(string symlinkOrStrmPath)
    {
        // if we already have the movie-id cached
        // then let's use it to find and return the corresponding movie-file-id
        if (SymlinkOrStrmToMovieIdCache.TryGetValue(symlinkOrStrmPath, out int movieId))
        {
            var movie = await GetMovieAsync(movieId);
            if (movie.MovieFile?.Path == symlinkOrStrmPath)
                return (movie.MovieFile.Id!, movieId);
        }

        // PERF NOTE: This fetches ALL movies to find one match
        // This is a trade-off: first call is expensive but populates cache for future calls
        // TODO: If Radarr API supports filtering by path, use that instead
        var allMovies = await GetMoviesAsync();
        (int movieFileId, int movieId)? result = null;
        foreach (var movie in allMovies)
        {
            var movieFile = movie.MovieFile;
            if (movieFile?.Path != null)
            {
                SymlinkOrStrmToMovieIdCache.Set(movieFile.Path, movie.Id, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24),
                    Size = 1
                });
            }
            if (movieFile?.Path == symlinkOrStrmPath)
                result = (movieFile.Id!, movie.Id);
        }

        return result;
    }
}