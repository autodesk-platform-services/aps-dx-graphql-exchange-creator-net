using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// Drives an exchange job through its steps. The job is advanced whenever the Viewer extension polls its status,
// using the tokens of the logged-in user, so that no refresh tokens need to be kept (and rotated) on the server.
public class ExchangePipeline
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(90);
    private const int MaxStatusErrors = 30; // about 2.5 minutes at the 5-second polling interval
    private readonly APS _aps;
    private readonly ConcurrentDictionary<string, ExchangeJob> _jobs = new();

    public ExchangePipeline(APS aps)
    {
        _aps = aps;
    }

    public ExchangeJob CreateJob(CreateJobRequest request)
    {
        var job = new ExchangeJob(request);
        _jobs[job.Id] = job;
        return job;
    }

    public ExchangeJob GetJob(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public async Task Advance(ExchangeJob job, Tokens tokens)
    {
        // If another request is already advancing this job, just report its current state
        if (job.IsFinished || !await job.Lock.WaitAsync(0))
        {
            return;
        }
        JobStep step = null;
        try
        {
            while (!job.IsFinished)
            {
                step = job.Steps[job.CurrentStep];
                if (step.Status == StepStatus.Pending)
                {
                    step.Status = StepStatus.InProgress;
                    step.StartedAt = DateTime.UtcNow;
                }
                if (!await RunStep(job, step, tokens))
                {
                    if (DateTime.UtcNow - step.StartedAt > StepTimeout)
                    {
                        throw new TimeoutException($"The step did not complete within {StepTimeout.TotalMinutes} minutes.");
                    }
                    break; // Waiting for an external service, check again on the next poll
                }
                step.Status = StepStatus.Completed;
                step.FinishedAt = DateTime.UtcNow;
                job.CurrentStep++;
                if (job.CurrentStep == job.Steps.Count)
                {
                    job.Status = JobStatus.Completed;
                }
            }
        }
        catch (Exception ex)
        {
            if (step != null)
            {
                step.Status = StepStatus.Failed;
                step.Details = ex.Message;
                step.FinishedAt = DateTime.UtcNow;
            }
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
        }
        finally
        {
            job.Lock.Release();
        }
    }

    // Returns true when the step is completed, false when it is still waiting for an external service
    private Task<bool> RunStep(ExchangeJob job, JobStep step, Tokens tokens) => step.Id switch
    {
        ExchangeJob.SelectionStep => SaveSelection(job, step, tokens),
        ExchangeJob.AutomationStep => PrepareAutomation(job, step),
        ExchangeJob.UploadStep => UploadDesign(job, step, tokens),
        ExchangeJob.ProcessingStep => WaitForWorkItem(job, step),
        ExchangeJob.ReceivedStep => ReceiveDesign(job, step, tokens),
        ExchangeJob.TranslationStep => WaitForTranslation(job, step, tokens),
        ExchangeJob.ExchangeStep => CreateExchange(job, step, tokens),
        _ => throw new InvalidOperationException($"Unknown step {step.Id}")
    };

    private async Task<bool> SaveSelection(ExchangeJob job, JobStep step, Tokens tokens)
    {
        var design = await _aps.GetDesignInfo(job.HubId, job.ProjectId, job.VersionId, tokens);
        if (!string.Equals(design.FileType, "rvt", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{design.FileName} is not a Revit file.");
        }
        if (design.StorageUrn == null)
        {
            throw new InvalidOperationException($"{design.FileName} has no downloadable file (cloud workshared models are not supported).");
        }
        job.FileName = design.FileName;
        job.FolderId = design.FolderId;
        job.Region = design.Region;
        job.StorageUrn = design.StorageUrn;
        if (job.SaveAsNewVersion)
        {
            // Versions of cloud workshared models can only be published from Revit, not uploaded through the API
            if (design.ItemType?.EndsWith(":C4RModel") == true)
            {
                throw new InvalidOperationException($"{design.FileName} is a cloud workshared model, which can't get new versions this way. Save the view in a new file instead.");
            }
            // Same name, same folder: the result becomes a new version of the source item
            job.TempFileName = design.FileName;
        }
        else
        {
            // One file per exchange: exchanges stay in sync with their source file, so exchanges sharing a file would all
            // be updated (overwritten) by every new run
            job.TempFileName = $"{Path.GetFileNameWithoutExtension(design.FileName)}_temp_{SafeFileName(job.ExchangeName)}{Path.GetExtension(design.FileName)}";
        }

        // An existing exchange with this name gets updated through its source file instead of being created again
        job.DataExchangeFolderId = await _aps.GetDataExchangeFolderId(job.HubId, job.ProjectId, job.FolderId, job.Region, tokens);
        var existing = await _aps.FindExchangeInFolder(job.DataExchangeFolderId, job.ExchangeName, job.Region, tokens);
        if (existing != null && job.SaveAsNewVersion)
        {
            // The existing exchange may come from another file, so there's no telling whether it would sync
            throw new InvalidOperationException($"An exchange named \"{job.ExchangeName}\" already exists in this folder. Choose another name.");
        }
        if (existing != null)
        {
            job.ExchangeId = existing.Id;
            job.ExistingExchangeVersion = await _aps.GetExchangeVersionNumber(existing.Id, job.Region, tokens);
            job.Steps.Last().Title = $"Updated the existing exchange \"{job.ExchangeName}\"";
        }

        // ACC only extracts the views listed in the file's publish settings, which the Revit API cannot modify.
        // The add-in therefore reuses one of the 3D views ACC extracted from the source design (see RevitPlugin/Commands.cs).
        var derivativeUrn = await _aps.GetDerivativeUrn(job.ProjectId, job.VersionId, tokens)
            ?? throw new InvalidOperationException($"{design.FileName} hasn't been processed by ACC yet.");
        var extractedViews = await _aps.GetViews(derivativeUrn, job.Region, tokens);
        await SelectRevitVersion(job, await _aps.GetRevitVersion(derivativeUrn, job.Region, tokens));
        // When saving a new version of the source file, nothing but a new view is added: no published view is repurposed,
        // and an existing view with the requested name is not replaced (the add-in fails instead)
        var published3DViews = job.SaveAsNewVersion
            ? new List<string>()
            : extractedViews.Where(view => view.Role == "3d" && !view.IsMasterView).Select(view => view.Name).ToList();

        var selection = JsonSerializer.Serialize(new
        {
            viewName = job.ViewName,
            externalIds = job.ExternalIds,
            publishedViews = published3DViews,
            replaceExistingView = !job.SaveAsNewVersion
        });
        job.SelectionUrl = await _aps.UploadAppObject($"{job.Id}/selection.json", selection);
        step.Details = $"{job.ElementCount} elements";
        return true;
    }

    // Picks the Design Automation engine: the Revit version the design was saved with, if the add-in was built for it and
    // the engine isn't retired, otherwise the closest newer usable one (the design then gets upgraded, which changes
    // how Data Exchange processes it, so the smallest possible upgrade is preferred)
    private async Task SelectRevitVersion(ExchangeJob job, int? sourceVersion)
    {
        var available = await _aps.GetUsableRevitVersions();
        if (available.Count == 0)
        {
            var built = _aps.GetAvailableRevitVersions();
            throw new InvalidOperationException(built.Count == 0
                ? "No app bundle found. Build the RevitPlugin project first (dotnet build RevitPlugin -c Release -p:RevitVersion=<version>)."
                : $"The add-in was only built for retired Design Automation engines ({string.Join(", ", built)}). Build it for a current Revit version.");
        }
        job.SourceRevitVersion = sourceVersion ?? available.Last();
        // Below the minimum, Data Exchange doesn't reliably create exchanges (see AutomationSettings.MinRevitVersion)
        var target = Math.Max(job.SourceRevitVersion, _aps.MinRevitVersion);
        job.RevitVersion = available.Contains(target)
            ? target
            : available.Where(version => version > target).DefaultIfEmpty(0).First();
        job.UpgradeReason = job.SourceRevitVersion < _aps.MinRevitVersion
            ? $"exchanges from files older than Revit {_aps.MinRevitVersion} don't complete reliably, see bug_report/README.md"
            : "the closest version available in Design Automation";
        if (job.RevitVersion == 0)
        {
            throw new InvalidOperationException($"{job.FileName} was saved with Revit {job.SourceRevitVersion}, which is newer than the add-in builds " +
                $"available ({string.Join(", ", available)}). Build it with: dotnet build RevitPlugin -c Release -p:RevitVersion={job.SourceRevitVersion}");
        }
    }

    private async Task<bool> PrepareAutomation(ExchangeJob job, JobStep step)
    {
        job.ActivityId = await _aps.EnsureActivity(job.RevitVersion);
        step.Details = job.RevitVersion == job.SourceRevitVersion
            ? $"Activity {job.ActivityId} (Revit {job.RevitVersion}, same as the design)"
            : $"Activity {job.ActivityId}: the design, saved with Revit {job.SourceRevitVersion}, will be upgraded to Revit {job.RevitVersion} " +
              $"({job.UpgradeReason})";
        if (job.SaveAsNewVersion && job.RevitVersion != job.SourceRevitVersion)
        {
            step.Details += $". WARNING: the new version of {job.FileName} will be saved with Revit {job.RevitVersion}; " +
                $"it can't be opened with Revit {job.SourceRevitVersion} (earlier versions stay available in ACC)";
        }
        return true;
    }

    private async Task<bool> UploadDesign(ExchangeJob job, JobStep step, Tokens tokens)
    {
        if (job.WorkItemId == null)
        {
            // Design Automation downloads the source design and uploads the result directly from/to ACC storage
            var (bucketKey, objectKey) = APS.ParseStorageUrn(job.StorageUrn);
            var inputUrl = await _aps.GetSignedDownloadUrl(bucketKey, objectKey, tokens.InternalToken);
            job.TempStorageUrn = await _aps.CreateStorage(job.ProjectId, job.FolderId, job.TempFileName, tokens);
            var (tempBucketKey, tempObjectKey) = APS.ParseStorageUrn(job.TempStorageUrn);
            var upload = await _aps.GetSignedUploadUrl(tempBucketKey, tempObjectKey, tokens.InternalToken);
            job.TempUploadKey = upload.UploadKey;

            job.WorkItemId = await _aps.CreateWorkItem(job.ActivityId, inputUrl, job.SelectionUrl, upload.Url);
            step.Details = $"{job.FileName}, work item {job.WorkItemId} queued";
            return false;
        }
        var workItem = await _aps.GetWorkItem(job.WorkItemId);
        if (workItem.Status == "pending")
        {
            return false;
        }
        step.Details = $"{job.FileName}, work item {job.WorkItemId}";
        return true;
    }

    private async Task<bool> WaitForWorkItem(ExchangeJob job, JobStep step)
    {
        var workItem = await _aps.GetWorkItem(job.WorkItemId);
        if (workItem.Status == "pending" || workItem.Status == "inprogress")
        {
            step.Details = $"Work item {workItem.Status}";
            return false;
        }
        if (workItem.ReportUrl != null && !job.Links.Any(link => link.Url == workItem.ReportUrl))
        {
            // Replaced rather than modified, since the job may be serialized by a concurrent status request
            job.Links = job.Links.Append(new JobLink { Label = "Design Automation report", Url = workItem.ReportUrl }).ToList();
        }
        var report = workItem.ReportUrl != null ? await _aps.GetWorkItemReport(workItem.ReportUrl) : "";
        if (workItem.Status != "success")
        {
            var errors = report.Split('\n').Where(line => line.Contains("ERROR")).Select(line => line.Trim()).Take(3);
            throw new InvalidOperationException($"Work item {workItem.Status}. {string.Join(" ", errors)} See the report for details.");
        }
        step.Details = ParseViewResult(report) ?? $"Saved as {job.TempFileName}";
        return true;
    }

    // The add-in writes a line "EXCHANGE_VIEW_RESULT: {json}" to the work item report
    private static string ParseViewResult(string report)
    {
        const string marker = "EXCHANGE_VIEW_RESULT:";
        var line = report.Split('\n').FirstOrDefault(l => l.Contains(marker));
        if (line == null)
        {
            return null;
        }
        using var result = JsonDocument.Parse(line.Substring(line.IndexOf(marker) + marker.Length).Trim());
        var isolated = result.RootElement.GetProperty("Isolated").GetInt32();
        var requested = result.RootElement.GetProperty("Requested").GetInt32();
        var details = isolated == requested
            ? $"All {isolated} elements isolated in the view"
            : $"{isolated} of {requested} elements isolated in the view ({requested - isolated} not found in the design)";
        if (result.RootElement.TryGetProperty("ReusedView", out var reusedView) && reusedView.ValueKind == JsonValueKind.String)
        {
            details += $" (reused the published view \"{reusedView.GetString()}\", renamed)";
        }
        if (result.RootElement.TryGetProperty("PublishedIn", out var publishedIn) && publishedIn.GetArrayLength() > 0)
        {
            details += $"; added to the published view/sheet set(s): {string.Join(", ", publishedIn.EnumerateArray().Select(set => set.GetString()))}";
        }
        return details;
    }

    private async Task<bool> ReceiveDesign(ExchangeJob job, JobStep step, Tokens tokens)
    {
        var (bucketKey, objectKey) = APS.ParseStorageUrn(job.TempStorageUrn);
        await _aps.CompleteSignedUpload(bucketKey, objectKey, job.TempUploadKey, tokens.InternalToken);
        var version = await _aps.CreateFileVersion(job.ProjectId, job.FolderId, job.TempFileName, job.TempStorageUrn, tokens);
        job.TempItemId = version.ItemId;
        job.TempVersionId = version.VersionId;
        step.Details = job.SaveAsNewVersion
            ? $"Saved as version {version.VersionNumber} of {job.FileName}"
            : $"Saved as {job.TempFileName} (version {version.VersionNumber}) next to {job.FileName}";
        return true;
    }

    private async Task<bool> WaitForTranslation(ExchangeJob job, JobStep step, Tokens tokens)
    {
        job.DerivativeUrn ??= await _aps.GetDerivativeUrn(job.ProjectId, job.TempVersionId, tokens);
        var manifest = job.DerivativeUrn != null ? await _aps.GetManifest(job.DerivativeUrn, job.Region, tokens) : null;
        if (manifest == null)
        {
            step.Details = "Waiting for ACC to start processing the design";
            return false;
        }
        // A finished manifest can report "failed" when only part of the processing failed while the views are fine,
        // so whether the needed view was extracted is what decides
        var finished = manifest.Progress == "complete" || manifest.Status is "success" or "failed" or "timeout";
        switch (finished ? "success" : manifest.Status)
        {
            case "success":
                // Data Exchange can only create exchanges from views ACC has extracted
                var views = (await _aps.GetViews(job.DerivativeUrn, job.Region, tokens)).Select(view => view.Name).ToList();
                if (!views.Contains(job.ViewName))
                {
                    throw new InvalidOperationException($"ACC did not extract the view \"{job.ViewName}\" (processing status: {manifest.Status}), so no exchange can be created from it. " +
                        $"ACC only extracts the views selected in the file's publish settings. Extracted views: {string.Join(", ", views)}.");
                }
                step.Details = $"View \"{job.ViewName}\" available";
                return true;
            default:
                step.Details = $"Processing ({manifest.Progress})";
                return false;
        }
    }

    // Characters ACC does not allow in file names
    private static string SafeFileName(string name)
        => string.Concat(name.Select(c => "\\/:*?\"<>|".Contains(c) ? '_' : c)).Trim();

    private async Task<bool> CreateExchange(ExchangeJob job, JobStep step, Tokens tokens)
    {
        if (job.ExistingExchangeVersion != null)
        {
            // The exchange syncs automatically with the new version of its source file
            var version = await _aps.GetExchangeVersionNumber(job.ExchangeId, job.Region, tokens);
            if (version > job.ExistingExchangeVersion)
            {
                step.Details = $"Exchange {job.ExchangeId} updated to version {version}";
                return true;
            }
            step.Details = $"Waiting for exchange {job.ExchangeId} to sync with {job.TempFileName} (currently version {version})";
            return false;
        }
        if (job.ExchangeId == null)
        {
            var folderId = job.DataExchangeFolderId;
            var fileId = await _aps.GetDataExchangeFileId(folderId, job.HubId, job.ProjectId, job.FolderId, job.TempItemId, job.TempFileName, job.Region, tokens);
            var exchange = await _aps.CreateExchange(fileId, folderId, job.ViewName, job.ExchangeName, job.Region, tokens);
            job.ExchangeId = exchange.Id;
            step.Details = $"Exchange {exchange.Id} is being created";
            return false;
        }
        ExchangeCreationStatus status;
        try
        {
            status = await _aps.GetExchangeCreationStatus(job.ExchangeId, job.Region, tokens);
            job.StatusErrors = 0;
        }
        catch (ApplicationException ex)
        {
            if (++job.StatusErrors >= MaxStatusErrors)
            {
                throw new InvalidOperationException($"Exchange {job.ExchangeId} was requested, but its status stayed unavailable: {ex.Message}");
            }
            // The status may not be available right after the creation was requested
            step.Details = $"Exchange {job.ExchangeId} requested; waiting for its status (attempt {job.StatusErrors}: {ex.Message})";
            return false;
        }
        switch (status.Status)
        {
            case "COMPLETED":
                step.Details = $"Exchange {job.ExchangeId} (version {status.VersionNumber})";
                return true;
            case "FAILED":
                throw new InvalidOperationException($"The exchange creation failed (exchange {job.ExchangeId}).");
            default:
                step.Details = $"Exchange {job.ExchangeId}: {status.Status}";
                return false;
        }
    }
}
