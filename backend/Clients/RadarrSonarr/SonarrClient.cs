using System.Net;
using Microsoft.Extensions.Caching.Memory;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class SonarrClient(string host, string apiKey) : ArrClient(host, apiKey)
{
    // PERF FIX #16: Replace unbounded static dictionaries with size-limited MemoryCache
    // to prevent memory leaks in long-running instances
    private static readonly MemoryCache SeriesPathToSeriesIdCache = new(new MemoryCacheOptions
    {
        SizeLimit = 1000, // Limit to 1000 series paths
        ExpirationScanFrequency = TimeSpan.FromHours(1)
    });

    private static readonly MemoryCache SymlinkOrStrmToEpisodeFileIdCache = new(new MemoryCacheOptions
    {
        SizeLimit = 5000, // Limit to 5000 episode files
        ExpirationScanFrequency = TimeSpan.FromHours(1)
    });

    public Task<SonarrQueue> GetSonarrQueueAsync() =>
        Get<SonarrQueue>($"/queue?protocol=usenet&pageSize=5000");

    public Task<List<SonarrSeries>> GetAllSeries() =>
        Get<List<SonarrSeries>>($"/series");

    public Task<SonarrSeries> GetSeries(int seriesId) =>
        Get<SonarrSeries>($"/series/{seriesId}");

    public Task<SonarrEpisodeFile> GetEpisodeFile(int episodeFileId) =>
        Get<SonarrEpisodeFile>($"/episodefile/{episodeFileId}");

    public Task<List<SonarrEpisodeFile>> GetAllEpisodeFiles(int seriesId) =>
        Get<List<SonarrEpisodeFile>>($"/episodefile?seriesId={seriesId}");

    public Task<List<SonarrEpisode>> GetEpisodesFromEpisodeFileId(int episodeFileId) =>
        Get<List<SonarrEpisode>>($"/episode?episodeFileId={episodeFileId}");

    public Task<HttpStatusCode> DeleteEpisodeFile(int episodeFileId) =>
        Delete($"/episodefile/{episodeFileId}");

    public Task<ArrCommand> SearchEpisodesAsync(List<int> episodeIds) =>
        CommandAsync(new { name = "EpisodeSearch", episodeIds });

    public override async Task<bool> RemoveAndSearch(string symlinkOrStrmPath)
    {
        // get episode-file-id and episode-ids
        var mediaIds = await GetMediaIds(symlinkOrStrmPath);
        if (mediaIds == null) return false;

        // delete the episode-file
        if (await DeleteEpisodeFile(mediaIds.Value.episodeFileId) != HttpStatusCode.OK)
            throw new Exception($"Failed to delete episode file `{symlinkOrStrmPath}` from sonarr instance `{Host}`.");

        // trigger a new search for each episode
        await SearchEpisodesAsync(mediaIds.Value.episodeIds);
        return true;
    }

    private async Task<(int episodeFileId, List<int> episodeIds)?> GetMediaIds(string symlinkOrStrmPath)
    {
        // get episode-file-id
        var episodeFileId = await GetEpisodeFileId(symlinkOrStrmPath);
        if (episodeFileId == null) return null;

        // get episode-ids
        var episodes = await GetEpisodesFromEpisodeFileId(episodeFileId.Value);
        var episodeIds = episodes.Select(x => x.Id).ToList();
        if (episodeIds.Count == 0) return null;

        // return
        return (episodeFileId.Value, episodeIds);
    }

    private async Task<int?> GetEpisodeFileId(string symlinkOrStrmPath)
    {
        // if episode-file-id is found in the cache, verify it and return it
        if (SymlinkOrStrmToEpisodeFileIdCache.TryGetValue(symlinkOrStrmPath, out int episodeFileId))
        {
            var episodeFile = await GetEpisodeFile(episodeFileId);
            if (episodeFile.Path == symlinkOrStrmPath) return episodeFileId;
        }

        // otherwise, find the series-id
        var seriesId = await GetSeriesId(symlinkOrStrmPath);
        if (seriesId == null) return null;

        // PERF NOTE: This fetches ALL episode files for the series to find one match
        // This is a trade-off: first call is expensive but populates cache for future calls
        // TODO: If Sonarr API supports filtering by path, use that instead
        int? result = null;
        foreach (var episodeFile in await GetAllEpisodeFiles(seriesId.Value))
        {
            SymlinkOrStrmToEpisodeFileIdCache.Set(episodeFile.Path!, episodeFile.Id, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24),
                Size = 1
            });
            if (episodeFile.Path == symlinkOrStrmPath)
                result = episodeFile.Id;
        }

        // return the found episode-file-id
        return result;
    }

    private async Task<int?> GetSeriesId(string symlinkOrStrmPath)
    {
        // get series-id from cache
        var cachedSeriesId = PathUtil.GetAllParentDirectories(symlinkOrStrmPath)
            .Select(path => SeriesPathToSeriesIdCache.TryGetValue(path, out int seriesId) ? (int?)seriesId : null)
            .FirstOrDefault(id => id != null);

        // if found, verify and return it
        if (cachedSeriesId != null)
        {
            var series = await GetSeries(cachedSeriesId.Value);
            if (symlinkOrStrmPath.StartsWith(series.Path!))
                return cachedSeriesId;
        }

        // PERF NOTE: This fetches ALL series to find one match
        // This is a trade-off: first call is expensive but populates cache for future calls
        // TODO: If Sonarr API supports filtering by path, use that instead
        int? result = null;
        foreach (var series in await GetAllSeries())
        {
            SeriesPathToSeriesIdCache.Set(series.Path!, series.Id, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24),
                Size = 1
            });
            if (symlinkOrStrmPath.StartsWith(series.Path!))
                result = series.Id;
        }

        // return the found series-id
        return result;
    }
}