using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;

public enum StepStatus { Pending, InProgress, Completed, Failed }

public enum JobStatus { InProgress, Completed, Failed }

public class JobStep
{
    public string Id { get; set; }
    public string Title { get; set; }
    public StepStatus Status { get; set; } = StepStatus.Pending;
    public string Details { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

public class JobLink
{
    public string Label { get; set; }
    public string Url { get; set; }
}

public class CreateJobRequest
{
    public string HubId { get; set; }
    public string ProjectId { get; set; }
    public string VersionId { get; set; }
    public string ViewName { get; set; }
    public string ExchangeName { get; set; }
    public List<string> ExternalIds { get; set; } = new();
    // "newFile" (default): save the view in <name>_temp_<exchange>.rvt; "newVersion": save it as a new version of the source file
    public string SaveMode { get; set; } = "newFile";
}

// State of one "selection set -> Revit view -> exchange" run, returned as-is to the Viewer extension
public class ExchangeJob
{
    public const string SelectionStep = "selection";
    public const string AutomationStep = "automation";
    public const string UploadStep = "upload";
    public const string ProcessingStep = "processing";
    public const string ReceivedStep = "received";
    public const string TranslationStep = "translation";
    public const string ExchangeStep = "exchange";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public JobStatus Status { get; set; } = JobStatus.InProgress;
    public string Error { get; set; }
    public string HubId { get; set; }
    public string ProjectId { get; set; }
    public string VersionId { get; set; }
    public string ViewName { get; set; }
    public string ExchangeName { get; set; }
    public int ElementCount { get; set; }
    public bool SaveAsNewVersion { get; set; }
    public string FileName { get; set; }
    public string TempFileName { get; set; }
    public string ExchangeId { get; set; }
    public List<JobLink> Links { get; set; } = new();
    public List<JobStep> Steps { get; set; }

    // Internal state carried from one step to the next
    [JsonIgnore] public List<string> ExternalIds;
    [JsonIgnore] public int CurrentStep;
    [JsonIgnore] public string SelectionUrl;
    [JsonIgnore] public string ActivityId;
    [JsonIgnore] public string FolderId;
    [JsonIgnore] public string Region;
    [JsonIgnore] public string StorageUrn;
    [JsonIgnore] public string TempStorageUrn;
    [JsonIgnore] public string TempUploadKey;
    [JsonIgnore] public string WorkItemId;
    [JsonIgnore] public string TempItemId;
    [JsonIgnore] public string TempVersionId;
    [JsonIgnore] public string DerivativeUrn;
    [JsonIgnore] public int StatusErrors;
    [JsonIgnore] public string DataExchangeFolderId;
    [JsonIgnore] public int SourceRevitVersion;
    [JsonIgnore] public int RevitVersion; // Version of the Design Automation engine used
    [JsonIgnore] public string UpgradeReason;
    // Set when an exchange with the requested name already exists: it's updated (synced to the new file version)
    // rather than created again
    [JsonIgnore] public int? ExistingExchangeVersion;
    [JsonIgnore] public readonly SemaphoreSlim Lock = new(1, 1);

    public ExchangeJob(CreateJobRequest request)
    {
        HubId = request.HubId;
        ProjectId = request.ProjectId;
        VersionId = request.VersionId;
        ViewName = request.ViewName.Trim();
        ExchangeName = request.ExchangeName.Trim();
        ExternalIds = request.ExternalIds.Distinct().ToList();
        ElementCount = ExternalIds.Count;
        SaveAsNewVersion = request.SaveMode == "newVersion";
        Steps = new List<JobStep>
        {
            new() { Id = SelectionStep, Title = "Saved the selection list" },
            new() { Id = AutomationStep, Title = "Prepared Design Automation (app bundle and activity)" },
            new() { Id = UploadStep, Title = "Uploaded the Revit design to Automation" },
            new() { Id = ProcessingStep, Title = $"Created the view \"{ViewName}\" in Revit" },
            new() { Id = ReceivedStep, Title = "Received the processed Revit design" },
            new() { Id = TranslationStep, Title = "Processed the new design in ACC" },
            new() { Id = ExchangeStep, Title = $"Created the exchange \"{ExchangeName}\"" }
        };
    }

    [JsonIgnore] public bool IsFinished => Status != JobStatus.InProgress;
}
