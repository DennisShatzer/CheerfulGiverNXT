using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CheerfulGiverNXT.Workflow;

/// <summary>
/// A single "workflow/transaction" object as the source of truth.
/// Captures: search -> select constituent -> enter gift -> SKY API result -> local SQL commit.
/// </summary>
public sealed class GiftWorkflowContext
{
    public Guid WorkflowId { get; init; } = Guid.NewGuid();

    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    public string MachineName { get; init; } = Environment.MachineName;
    public string WindowsUser { get; init; } = Environment.UserName;

    public string? SearchText { get; set; }

    public ConstituentSnapshot Constituent { get; set; } = new();

    public GiftDraft Gift { get; set; } = new();

    public bool? IsFirstTimeGiver { get; set; }
    public bool? IsNewRadioConstituent { get; set; }

    public ApiResult Api { get; set; } = new();

    public WorkflowStatus Status { get; set; } = WorkflowStatus.Draft;

    /// <summary>
    /// Append-only status/event trail for auditing and retry scenarios.
    /// This is stored inside ContextJson, so it can evolve without schema changes.
    /// </summary>
    public List<WorkflowStatusTrailEntry>? StatusTrail { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions.Default);

    public static GiftWorkflowContext Start(string? searchText, ConstituentSnapshot snapshot)
    {
        return new GiftWorkflowContext
        {
            SearchText = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim(),
            Constituent = snapshot
        };
    }

    public void AddTrail(string @event, string? note = null)
    {
        if (string.IsNullOrWhiteSpace(@event))
            return;

        StatusTrail ??= new List<WorkflowStatusTrailEntry>();
        StatusTrail.Add(new WorkflowStatusTrailEntry
        {
            AtUtc = DateTime.UtcNow,
            Event = @event.Trim(),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        });
    }
}

public enum WorkflowStatus
{
    Draft = 0,
    ReadyToSubmit = 10,
    ApiSucceeded = 20,
    ApiFailed = 30,
    Committed = 40,
    CommitFailed = 50
}

public sealed class ConstituentSnapshot
{
    public int ConstituentId { get; set; }
    public string? FullName { get; set; }
    public string? Spouse { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
}

public sealed class GiftDraft
{
    public decimal Amount { get; set; }
    public string? Frequency { get; set; }
    public int? Installments { get; set; }
    public DateTime? PledgeDate { get; set; }
    public DateTime? StartDate { get; set; }

    public string? FundId { get; set; }
    public string? CampaignId { get; set; }
    public string? AppealId { get; set; }
    public string? PackageId { get; set; }

    public bool SendReminder { get; set; }
    public string? Comments { get; set; }

    public SponsorshipDraft Sponsorship { get; set; } = new();
}

public sealed class SponsorshipDraft
{
    public bool IsEnabled { get; set; }
    public DateTime? SponsoredDate { get; set; }
    public string? Slot { get; set; }
    public decimal? ThresholdAmount { get; set; }
}

public sealed class ApiResult
{
    public bool Attempted { get; set; }
    public DateTime? AttemptedAtUtc { get; set; }

    public bool Success { get; set; }
    public string? GiftId { get; set; }

    public string? ErrorMessage { get; set; }

    public string? RequestJson { get; set; }
    public string? CreateResponseJson { get; set; }
    public string? InstallmentListJson { get; set; }
    public string? InstallmentAddJson { get; set; }
}

public sealed class WorkflowStatusTrailEntry
{
    public DateTime AtUtc { get; set; }
    public string Event { get; set; } = "";
    public string? Note { get; set; }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}
