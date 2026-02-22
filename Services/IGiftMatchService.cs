using CheerfulGiverNXT.Workflow;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Services;

public sealed record MatchChallengeAdminRow(
    int ChallengeRecordId,
    string Name,
    decimal Budget,
    decimal Used,
    decimal Remaining,
    DateTime CreatedAtUtc,
    bool IsActive
);

public sealed record MatchAdminSnapshot(
    int? CampaignRecordId,
    int? AnonymousMatchConstituentId,
    MatchChallengeAdminRow[] Challenges
);

public sealed record MatchAllocation(
    int ChallengeRecordId,
    string ChallengeName,
    decimal AmountMatched,
    string? MatchedApiGiftId
);

public sealed record MatchApplyResult(
    decimal TotalMatched,
    MatchAllocation[] Allocations,
    string[] Warnings
);

public interface IGiftMatchService
{
    Task<MatchAdminSnapshot> GetAdminSnapshotAsync(CancellationToken ct = default);

    Task SetAnonymousMatchConstituentIdAsync(int constituentId, CancellationToken ct = default);

    Task CreateMatchChallengeAsync(string name, decimal budget, CancellationToken ct = default);

    Task DeactivateChallengeAsync(int challengeRecordId, CancellationToken ct = default);

    /// <summary>
    /// Applies matching-gift challenges to a successfully created pledge.
    /// This is best-effort: it should never throw just because no match is configured.
    /// </summary>
    Task<MatchApplyResult> ApplyMatchesForGiftAsync(GiftWorkflowContext sourceWorkflow, CancellationToken ct = default);
}
