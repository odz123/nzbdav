using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.UpdateConfig;

[ApiController]
[Route("api/update-config")]
public class UpdateConfigController(DavDatabaseClient dbClient, ConfigManager configManager, ILogger<UpdateConfigController> logger) : BaseApiController
{
    private async Task<UpdateConfigResponse> UpdateConfig(UpdateConfigRequest request)
    {
        // Validate usenet.servers configuration if present
        var usenetServersConfig = request.ConfigItems.FirstOrDefault(x => x.ConfigName == "usenet.servers");
        if (usenetServersConfig != null && !string.IsNullOrWhiteSpace(usenetServersConfig.ConfigValue))
        {
            try
            {
                logger.LogInformation("Received usenet.servers config: {Json}", usenetServersConfig.ConfigValue);

                var options = new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                };

                var servers = System.Text.Json.JsonSerializer.Deserialize<List<Clients.Usenet.Models.UsenetServerConfig>>(usenetServersConfig.ConfigValue, options);
                
                logger.LogInformation("Deserialized {Count} servers", servers?.Count ?? 0);

                if (servers != null)
                {
                    var invalidServers = servers
                        .Where(s => s.Enabled && string.IsNullOrWhiteSpace(s.Host))
                        .ToList();
                    
                    if (invalidServers.Any())
                    {
                        // Automatically disable invalid servers instead of rejecting the request
                        foreach (var invalidServer in invalidServers)
                        {
                            var serverToFix = servers.First(s => s.Id == invalidServer.Id);
                            serverToFix.Enabled = false;
                        }
                        // Update the config value with the fixed list
                        usenetServersConfig.ConfigValue = System.Text.Json.JsonSerializer.Serialize(servers, options);
                    }
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new BadHttpRequestException($"Invalid JSON format for usenet.servers configuration: {ex.Message}");
            }
        }

        // 1. Retrieve all ConfigItems from the database that match the ConfigNames in the request
        var configNames = request.ConfigItems.Select(x => x.ConfigName).ToHashSet();
        var existingItems = await dbClient.Ctx.ConfigItems
            .Where(c => configNames.Contains(c.ConfigName))
            .ToListAsync(HttpContext.RequestAborted);

        // 2. Split the items into those that need to be updated and those that need to be inserted
        var existingItemsDict = existingItems.ToDictionary(i => i.ConfigName);
        var itemsToUpdate = new List<ConfigItem>();
        var itemsToInsert = new List<ConfigItem>();
        foreach (var item in request.ConfigItems)
        {
            if (existingItemsDict.TryGetValue(item.ConfigName, out ConfigItem? existingItem))
            {
                existingItem.ConfigValue = item.ConfigValue;
                itemsToUpdate.Add(existingItem);
            }
            else
            {
                itemsToInsert.Add(item);
            }
        }

        // 3. Perform bulk insert and bulk update
        dbClient.Ctx.ConfigItems.AddRange(itemsToInsert);
        dbClient.Ctx.ConfigItems.UpdateRange(itemsToUpdate);

        // 4. Save changes in one call
        await dbClient.Ctx.SaveChangesAsync(HttpContext.RequestAborted);

        // 5. Update the ConfigManager
        await configManager.UpdateValuesAsync(request.ConfigItems);

        // return
        return new UpdateConfigResponse { Status = true };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new UpdateConfigRequest(HttpContext);
        var response = await UpdateConfig(request);
        return Ok(response);
    }
}