using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CheerfulGiverNXT.Services;

namespace CheerfulGiverSQP.SkyQueue;

public sealed class SkyTransactionProcessor
{
    private readonly SqlSkyTransactionRepository _repository;
    private readonly RenxtGiftServer _skyGiftServer;
    private readonly string _workerLabel;
    private readonly ProcessingOptions _options;

    public SkyTransactionProcessor(
        SqlSkyTransactionRepository repository,
        RenxtGiftServer skyGiftServer,
        string workerLabel,
        ProcessingOptions options)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _skyGiftServer = skyGiftServer ?? throw new ArgumentNullException(nameof(skyGiftServer));
        _workerLabel = string.IsNullOrWhiteSpace(workerLabel) ? "CheerfulGiverSQP" : workerLabel.Trim();
        _options = options;
    }

    public async Task<int> ProcessOnceAsync(Action<string> log, CancellationToken ct)
    {
        // Claim a batch
        var batch = await _repository.ClaimPendingBatchAsync(
            batchSize: _options.BatchSize,
            workerLabel: _workerLabel,
            staleProcessingMinutes: _options.StaleProcessingMinutes,
            maxAttempts: _options.MaxAttempts,
            ct: ct).ConfigureAwait(false);

        if (batch.Count == 0)
            return 0;

        log($"Claimed {batch.Count} transaction(s).");

        var processed = 0;

        foreach (var row in batch)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!string.Equals(row.TransactionType, "PledgeCreate", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Unsupported TransactionType '{row.TransactionType}'.");

                var req = JsonSerializer.Deserialize<RenxtGiftServer.CreatePledgeRequest>(row.RequestJson);
                if (req is null)
                    throw new InvalidOperationException("RequestJson could not be deserialized as CreatePledgeRequest.");

                var result = await _skyGiftServer.CreatePledgeAsync(req, ct).ConfigureAwait(false);

                await _repository.MarkSucceededAsync(
                    skyTransactionRecordId: row.SkyTransactionRecordId,
                    processedGiftId: result.GiftId,
                    statusNote: $"Posted by {_workerLabel}. GiftId={result.GiftId}",
                    ct: ct).ConfigureAwait(false);

                log($"Succeeded: QueueId={row.SkyTransactionRecordId}, GiftId={result.GiftId}");
                processed++;
            }
            catch (Exception ex)
            {
                // Do not include raw JSON in error fields; keep it concise.
                var msg = ex.Message;

                await _repository.MarkFailedAsync(
                    skyTransactionRecordId: row.SkyTransactionRecordId,
                    errorMessage: msg,
                    statusNote: $"Failed by {_workerLabel}.",
                    ct: ct).ConfigureAwait(false);

                log($"Failed: QueueId={row.SkyTransactionRecordId}, Error={msg}");
            }
        }

        return processed;
    }
}
