using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

public class WorkItemStatus
{
    public string Id;
    public string Status;
    public string ReportUrl;
}

public partial class APS
{
    private const string DesignAutomationUrl = "/da/us-east/v3";
    private const string Alias = "prod";
    private const string BundleDescription = "Creates a Revit view isolating a given selection set";
    private readonly SemaphoreSlim _automationSetupLock = new(1, 1);
    private string _nickname;

    // One app bundle and one activity per Revit version, so that each design is processed with the engine of the
    // Revit version it was saved with: an upgraded design behaves differently (in Data Exchange, rooms appeared)
    private static string BundleName(int revitVersion) => $"ExchangeViewCreator{revitVersion}";
    private static string ActivityName(int revitVersion) => $"ExchangeViewCreatorActivity{revitVersion}";
    private static string Engine(int revitVersion) => $"Autodesk.Revit+{revitVersion}";

    private string BundlePath(int revitVersion) => Path.Combine(_automationSettings.BundlesPath, revitVersion.ToString(), "ExchangeViewCreator.zip");

    // Revit versions the add-in was built for (RevitPlugin/bin/Release/<version>/ExchangeViewCreator.zip)
    public List<int> GetAvailableRevitVersions()
    {
        if (!Directory.Exists(_automationSettings.BundlesPath))
        {
            return new List<int>();
        }
        return Directory.GetDirectories(_automationSettings.BundlesPath)
            .Select(dir => int.TryParse(Path.GetFileName(dir), out var version) ? version : 0)
            .Where(version => version > 0 && File.Exists(BundlePath(version)))
            .OrderBy(version => version)
            .ToList();
    }

    // Design Automation retires each engine about three years after its release (new bundles and activities are
    // then rejected with "Engine ... is deprecated"), so only engines whose deprecation date is ahead can be used
    public async Task<List<int>> GetUsableRevitVersions()
    {
        var accessToken = await GetAppToken();
        var usable = new List<int>();
        foreach (var revitVersion in GetAvailableRevitVersions())
        {
            try
            {
                var engine = await GetAsync($"{DesignAutomationUrl}/engines/{Engine(revitVersion)}", accessToken);
                var deprecation = (string)engine["deprecationDate"];
                if (deprecation == null || DateTime.Parse(deprecation, System.Globalization.CultureInfo.InvariantCulture) > DateTime.UtcNow)
                {
                    usable.Add(revitVersion);
                }
            }
            catch (APSException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Not offered by Design Automation (yet)
            }
        }
        return usable;
    }

    private async Task<string> GetNickname(string accessToken)
    {
        // The nickname defaults to the client ID unless one was explicitly set for the application
        _nickname ??= (string)await GetAsync($"{DesignAutomationUrl}/forgeapps/me", accessToken);
        return _nickname;
    }

    // The hash of the zip is kept in the app bundle description, to detect when the local bundle was rebuilt
    private string LocalBundleDescription(int revitVersion)
    {
        var bundlePath = BundlePath(revitVersion);
        if (!File.Exists(bundlePath))
        {
            throw new FileNotFoundException($"App bundle not found at {bundlePath}. Build the RevitPlugin project first (dotnet build RevitPlugin -c Release -p:RevitVersion={revitVersion}).");
        }
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bundlePath)));
        return $"{BundleDescription} (sha256 {hash})";
    }

    // Makes sure the app bundle and the activity for the given Revit version exist and the bundle matches the local zip;
    // with force=true, uploads the local bundle and updates both regardless
    public async Task<string> EnsureActivity(int revitVersion, bool force = false)
    {
        await _automationSetupLock.WaitAsync();
        try
        {
            var accessToken = await GetAppToken();
            var nickname = await GetNickname(accessToken);
            var activityId = $"{nickname}.{ActivityName(revitVersion)}+{Alias}";
            if (!force)
            {
                try
                {
                    await GetAsync($"{DesignAutomationUrl}/activities/{activityId}", accessToken);
                    var bundle = await GetAsync($"{DesignAutomationUrl}/appbundles/{nickname}.{BundleName(revitVersion)}+{Alias}", accessToken);
                    if ((string)bundle["description"] == LocalBundleDescription(revitVersion))
                    {
                        return activityId;
                    }
                }
                catch (APSException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // Not set up yet
                }
            }
            await PublishAppBundle(revitVersion, accessToken);
            await PublishActivity(revitVersion, nickname, accessToken);
            return activityId;
        }
        finally
        {
            _automationSetupLock.Release();
        }
    }

    private async Task PublishAppBundle(int revitVersion, string accessToken)
    {
        var bundleName = BundleName(revitVersion);
        var body = new JsonObject
        {
            ["id"] = bundleName,
            ["engine"] = Engine(revitVersion),
            ["description"] = LocalBundleDescription(revitVersion)
        };
        JsonNode bundle;
        try
        {
            bundle = await PostAsync($"{DesignAutomationUrl}/appbundles", accessToken, body);
        }
        catch (APSException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            body.Remove("id");
            bundle = await PostAsync($"{DesignAutomationUrl}/appbundles/{bundleName}/versions", accessToken, body);
        }

        // Upload the zip using the (pre-signed) form data provided by Design Automation
        var uploadParameters = bundle["uploadParameters"];
        using var form = new MultipartFormDataContent();
        foreach (var field in uploadParameters["formData"].AsObject())
        {
            form.Add(new StringContent((string)field.Value), field.Key);
        }
        var bundlePath = BundlePath(revitVersion);
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(bundlePath)), "file", Path.GetFileName(bundlePath));
        using (var response = await _httpClient.PostAsync((string)uploadParameters["endpointURL"], form))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new APSException(response.StatusCode, $"Could not upload the app bundle: {await response.Content.ReadAsStringAsync()}");
            }
        }
        await SetAlias($"{DesignAutomationUrl}/appbundles/{bundleName}", (int)bundle["version"], accessToken);
    }

    private async Task PublishActivity(int revitVersion, string nickname, string accessToken)
    {
        var bundleName = BundleName(revitVersion);
        var activityName = ActivityName(revitVersion);
        var body = new JsonObject
        {
            ["id"] = activityName,
            ["commandLine"] = new JsonArray($"$(engine.path)\\revitcoreconsole.exe /i \"$(args[inputFile].path)\" /al \"$(appbundles[{bundleName}].path)\""),
            ["parameters"] = new JsonObject
            {
                ["inputFile"] = new JsonObject { ["verb"] = "get", ["localName"] = "input.rvt", ["required"] = true, ["description"] = "Source Revit design" },
                ["selection"] = new JsonObject { ["verb"] = "get", ["localName"] = "selection.json", ["required"] = true, ["description"] = "View name and external IDs of the elements to isolate" },
                ["result"] = new JsonObject { ["verb"] = "put", ["localName"] = "result.rvt", ["required"] = true, ["description"] = "Revit design with the new view" }
            },
            ["engine"] = Engine(revitVersion),
            ["appbundles"] = new JsonArray($"{nickname}.{bundleName}+{Alias}"),
            ["description"] = BundleDescription
        };
        JsonNode activity;
        try
        {
            activity = await PostAsync($"{DesignAutomationUrl}/activities", accessToken, body);
        }
        catch (APSException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            body.Remove("id");
            activity = await PostAsync($"{DesignAutomationUrl}/activities/{activityName}/versions", accessToken, body);
        }
        await SetAlias($"{DesignAutomationUrl}/activities/{activityName}", (int)activity["version"], accessToken);
    }

    private static async Task SetAlias(string resourceUrl, int version, string accessToken)
    {
        try
        {
            await PostAsync($"{resourceUrl}/aliases", accessToken, new JsonObject { ["id"] = Alias, ["version"] = version });
        }
        catch (APSException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            await SendAsync(HttpMethod.Patch, $"{resourceUrl}/aliases/{Alias}", accessToken, new JsonObject { ["version"] = version });
        }
    }

    public async Task<string> CreateWorkItem(string activityId, string inputFileUrl, string selectionUrl, string resultUrl)
    {
        var body = new JsonObject
        {
            ["activityId"] = activityId,
            ["arguments"] = new JsonObject
            {
                ["inputFile"] = new JsonObject { ["url"] = inputFileUrl, ["verb"] = "get" },
                ["selection"] = new JsonObject { ["url"] = selectionUrl, ["verb"] = "get" },
                ["result"] = new JsonObject { ["url"] = resultUrl, ["verb"] = "put" }
            }
        };
        var workItem = await PostAsync($"{DesignAutomationUrl}/workitems", await GetAppToken(), body);
        return (string)workItem["id"];
    }

    public async Task<WorkItemStatus> GetWorkItem(string workItemId)
    {
        var workItem = await GetAsync($"{DesignAutomationUrl}/workitems/{workItemId}", await GetAppToken());
        return new WorkItemStatus
        {
            Id = workItemId,
            Status = (string)workItem["status"],
            ReportUrl = (string)workItem["reportUrl"]
        };
    }

    public async Task<string> GetWorkItemReport(string reportUrl)
    {
        return await _httpClient.GetStringAsync(reportUrl);
    }
}
