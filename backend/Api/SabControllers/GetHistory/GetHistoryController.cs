using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.SabControllers.GetHistory;

public class GetHistoryController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    private async Task<GetHistoryResponse> GetHistoryAsync(GetHistoryRequest request)
    {
        // get query
        IQueryable<HistoryItem> query = dbClient.Ctx.HistoryItems;
        if (request.NzoIds.Count > 0)
            query = query.Where(q => request.NzoIds.Contains(q.Id));
        if (request.Category != null)
            query = query.Where(q => q.Category == request.Category);

        // OPTIMIZATION: Use single query with LEFT JOIN instead of 3 separate queries
        // Fetch history items with their download directories in one go
        var results = await (
            from h in query.OrderByDescending(q => q.CreatedAt).Skip(request.Start).Take(request.Limit)
            join d in dbClient.Ctx.Items on h.DownloadDirId equals d.Id into downloadDirs
            from dir in downloadDirs.DefaultIfEmpty()
            select new { HistoryItem = h, DownloadDir = dir }
        ).ToArrayAsync(request.CancellationToken);

        // Get total count in parallel (we still need this for pagination)
        var totalCount = await query.CountAsync(request.CancellationToken);

        // Build response slots
        var slots = results
            .Select(x =>
                GetHistoryResponse.HistorySlot.FromHistoryItem(
                    x.HistoryItem,
                    x.DownloadDir,
                    configManager
                )
            )
            .ToList();

        // return response
        return new GetHistoryResponse()
        {
            History = new GetHistoryResponse.HistoryObject()
            {
                Slots = slots,
                TotalCount = totalCount,
            }
        };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = new GetHistoryRequest(httpContext, configManager);
        return Ok(await GetHistoryAsync(request));
    }
}