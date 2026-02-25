using System;

namespace CheerfulGiverSQP.SkyQueue;

public sealed record SkyTransactionRow(
    int SkyTransactionRecordId,
    Guid WorkflowId,
    string TransactionType,
    string TransactionStatus,
    string? StatusNote,

    DateTime EnqueuedAtUtc,
    DateTime EnqueuedAtLocal,

    string ClientMachineName,
    string ClientWindowsUser,

    int ConstituentId,
    decimal Amount,
    DateTime PledgeDate,
    string FundId,
    string? Comments,

    string RequestJson,

    int ProcessingAttemptCount,
    DateTime? ProcessingStartedAtUtc,
    DateTime? ProcessingStartedAtLocal,
    DateTime? ProcessingCompletedAtUtc,
    DateTime? ProcessingCompletedAtLocal,
    DateTime? LastProcessingAttemptAtUtc,
    DateTime? LastProcessingAttemptAtLocal,
    string? LastProcessingErrorMessage,
    string? ProcessedGiftId
);
