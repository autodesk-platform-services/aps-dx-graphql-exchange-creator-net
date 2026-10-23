using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

public class Exchange
{
    public string Id;
    public string Name;
}

public class ExchangeCreationStatus
{
    public string Status;
    public int? VersionNumber;
}

public partial class APS
{
    private const string DataExchangeUrl = "https://developer.api.autodesk.com/dataexchange/2023-05/graphql";

    // operation names the call in error messages, since several calls can fail with the same generic error
    private static async Task<JsonNode> QueryDataExchange(string operation, string query, JsonObject variables, string region, Tokens tokens)
    {
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(region) && region != "US")
        {
            headers["Region"] = region;
        }
        var result = await PostAsync(DataExchangeUrl, tokens.InternalToken, new JsonObject { ["query"] = query, ["variables"] = variables }, headers: headers);
        var errors = result["errors"]?.AsArray();
        if (errors != null && errors.Count > 0)
        {
            // Include the path and the error code (extensions) when present, they tell more than the message
            throw new ApplicationException($"Data Exchange {operation} failed: " + string.Join("; ", errors.Select(error =>
            {
                var details = new[] { error["path"]?.ToJsonString(), error["extensions"]?.ToJsonString() }.Where(d => d != null);
                return $"{(string)error["message"]} {string.Join(" ", details)}".Trim();
            })));
        }
        return result["data"];
    }

    // Data Exchange IDs are base64-encoded strings such as "fold~<hub>~<project>~<folder>" or
    // "item~<hub>~<project>~<folder>~<item>", built from the Data Management IDs
    private static IEnumerable<string> DataExchangeIdCandidates(params string[] parts)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("~", parts)));
        return new[] { base64.TrimEnd('=').Replace('+', '-').Replace('/', '_'), base64.TrimEnd('='), base64 }.Distinct();
    }

    public async Task<string> GetDataExchangeFolderId(string hubId, string projectId, string folderId, string region, Tokens tokens)
    {
        const string query = "query GetFolder($folderId: ID!) { folder(folderId: $folderId) { id name } }";
        var errors = new List<string>();
        foreach (var candidate in DataExchangeIdCandidates("fold", hubId, projectId, folderId))
        {
            try
            {
                var data = await QueryDataExchange("folder lookup", query, new JsonObject { ["folderId"] = candidate }, region, tokens);
                var id = (string)data?["folder"]?["id"];
                if (id != null)
                {
                    return id;
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }
        throw new ApplicationException($"Could not find the folder in the Data Exchange API. {string.Join(" ", errors.Distinct())}");
    }

    public async Task<string> GetDataExchangeFileId(string dataExchangeFolderId, string hubId, string projectId, string folderId, string itemId, string fileName, string region, Tokens tokens)
    {
        const string query = @"query GetFolderItems($folderId: ID!) {
            folder(folderId: $folderId) {
                items {
                    ... on Items {
                        results { id name }
                    }
                }
            }
        }";
        try
        {
            var data = await QueryDataExchange("folder items", query, new JsonObject { ["folderId"] = dataExchangeFolderId }, region, tokens);
            var match = data["folder"]["items"]["results"].AsArray().FirstOrDefault(item => (string)item["name"] == fileName);
            if (match != null)
            {
                return (string)match["id"];
            }
        }
        catch (Exception)
        {
            // Fall back to building the ID ourselves
        }
        // Use the same encoding the API accepted for the folder ID
        var folderCandidates = DataExchangeIdCandidates("fold", hubId, projectId, folderId).ToList();
        var itemCandidates = DataExchangeIdCandidates("item", hubId, projectId, folderId, itemId).ToList();
        var index = folderCandidates.IndexOf(dataExchangeFolderId);
        return itemCandidates[index >= 0 ? index : 0];
    }

    public async Task<Exchange> FindExchangeInFolder(string dataExchangeFolderId, string exchangeName, string region, Tokens tokens)
    {
        const string query = "query GetFolderExchanges($folderId: ID!) { folder(folderId: $folderId) { exchanges { results { id name } } } }";
        var data = await QueryDataExchange("folder exchanges", query, new JsonObject { ["folderId"] = dataExchangeFolderId }, region, tokens);
        var match = data["folder"]["exchanges"]["results"].AsArray().FirstOrDefault(exchange => (string)exchange["name"] == exchangeName);
        return match == null ? null : new Exchange { Id = (string)match["id"], Name = (string)match["name"] };
    }

    public async Task<int> GetExchangeVersionNumber(string exchangeId, string region, Tokens tokens)
    {
        const string query = "query GetExchangeVersion($exchangeId: ID!) { exchange(exchangeId: $exchangeId) { version { versionNumber } } }";
        var data = await QueryDataExchange("exchange version", query, new JsonObject { ["exchangeId"] = exchangeId }, region, tokens);
        return (int)data["exchange"]["version"]["versionNumber"];
    }

    public async Task<Exchange> CreateExchange(string fileId, string folderId, string viewName, string exchangeName, string region, Tokens tokens)
    {
        const string mutation = @"mutation CreateExchangeFromRevit($viewName: String, $sourceFileId: String!, $targetExchangeName: String!, $targetFolderId: String!) {
            createExchange(
                input: {
                    viewName: $viewName
                    source: { fileId: $sourceFileId }
                    target: { name: $targetExchangeName, folderId: $targetFolderId }
                }
            ) {
                exchange { id name }
            }
        }";
        var variables = new JsonObject
        {
            ["viewName"] = viewName,
            ["sourceFileId"] = fileId,
            ["targetExchangeName"] = exchangeName,
            ["targetFolderId"] = folderId
        };
        var data = await QueryDataExchange("createExchange", mutation, variables, region, tokens);
        var exchange = data["createExchange"]["exchange"];
        return new Exchange { Id = (string)exchange["id"], Name = (string)exchange["name"] };
    }

    public async Task<ExchangeCreationStatus> GetExchangeCreationStatus(string exchangeId, string region, Tokens tokens)
    {
        const string query = @"query GetExchangeStatus($exchangeId: ID!) {
            getExchangeCreationStatus(exchangeId: $exchangeId) {
                status
                versionNumber
            }
        }";
        var data = await QueryDataExchange("getExchangeCreationStatus", query, new JsonObject { ["exchangeId"] = exchangeId }, region, tokens);
        var status = data["getExchangeCreationStatus"];
        return new ExchangeCreationStatus { Status = (string)status["status"], VersionNumber = (int?)status["versionNumber"] };
    }
}
