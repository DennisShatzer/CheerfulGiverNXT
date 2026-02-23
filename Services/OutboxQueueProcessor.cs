using CheerfulGiverNXT.Data;
using CheerfulGiverNXT.Infrastructure.Logging;
using CheerfulGiverNXT.Workflow;
using Microsoft.Data.SqlClient;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CheerfulGiverNXT.Infrastructure.AppMode;

namespace CheerfulGiverNXT.Services;

/// <summary>
/// Background outbox processor:
/// - Polls local SQL for workflows whose last API attempt failed.
/// - Retries submission to SKY using the stored Api.RequestJson.
/// - Records an append-only status trail in ContextJson.
///
/// NOTE: This implementation intentionally avoids requiring extra members on IGiftWorkflowStore
/// and avoids requiring GiftWorkflowContext.Outbox. All retry state is persisted via StatusTrail.
/// </summary>
public sealed class OutboxQueueProcessor : IDisposable
{
    private readonly string _connectionString;
    private readonly IGiftWorkflowStore _workflowStore;
    private readonly RenxtGiftServer _giftServer;
    private readonly IGiftMatchService _giftMatch;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public OutboxQueueProcessor(
        string connectionString,
        IGiftWorkflowStore workflowStore,
        RenxtGiftServer giftServer,
        IGiftMatchService giftMatch)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _workflowStore = workflowStore ?? throw new ArgumentNullException(nameof(workflowStore));
        _giftServer = giftServer ?? throw new ArgumentNullException(nameof(giftServer));
        _giftMatch = giftMatch ?? throw new ArgumentNullException(nameof(giftMatch));
    }

    private static void UpdateStatus(Action<OutboxQueueStatus> update)
    {
        try
        {
            var status = OutboxQueueStatus.Instance;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(new Action(() => update(status)));
            else
                update(status);
        }
        catch
        {
            // ignore (status is non-critical)
        }
    }

    public void Start()
    {
        if (_loop is not null) return;

        // Demo mode must never post to SKY API.
        if (!SkyPostingPolicy.IsPostingAllowed(out var reason))
        {
            UpdateStatus(s =>
            {
                s.IsRunning = false;
                s.LastMessage = "Suppressed: " + (reason ?? "posting disabled");
            });
            return;
        }

        if (!ReadBool("Outbox.Enabled", defaultValue: true))
            return;

        UpdateStatus(s => { s.IsRunning = true; s.LastMessage = "Running"; });

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        UpdateStatus(s => { s.IsRunning = false; s.LastMessage = "Stopped"; });
        try { _cts?.Cancel(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var initialDelay = TimeSpan.FromSeconds(ReadInt("Outbox.InitialDelaySeconds", 10, min: 0, max: 600));
        var poll = TimeSpan.FromSeconds(ReadInt("Outbox.PollSeconds", 60, min: 10, max: 3600));

        try
        {
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // If Demo mode is enabled while running, fail-closed.
                if (!SkyPostingPolicy.IsPostingAllowed(out var reason))
                {
                    UpdateStatus(s => { s.LastRunUtc = DateTime.UtcNow; s.LastMessage = "Suppressed: " + (reason ?? "posting disabled"); });
                }
                else
                {
                await ProcessOnceAsync(ct).ConfigureAwait(false);
                UpdateStatus(s => { s.LastRunUtc = DateTime.UtcNow; s.LastMessage = "Checked"; });
                }
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                UpdateStatus(s => { s.LastRunUtc = DateTime.UtcNow; s.LastMessage = ex.Message; });
                try { _ = ErrorLogger.Log(ex, "OutboxQueueProcessor.Loop"); } catch { /* ignore */ }
            }

            try
            {
                await Task.Delay(poll, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ProcessOnceAsync(CancellationToken ct)
    {
        var batchSize = ReadInt("Outbox.BatchSize", 3, min: 1, max: 25);

        // Candidate selection is intentionally broad (SQL does not inspect ContextJson).
        // Per-item logic enforces backoff / suppression.
        var ids = await ListOutboxCandidateWorkflowIdsAsync(batchSize * 5, ct).ConfigureAwait(false);
        if (ids.Length == 0)
            return;

        int processed = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (processed >= batchSize)
                break;

            // Prevent multiple app instances from retrying the same workflow simultaneously.
            await using var lockConn = new SqlConnection(_connectionString);
            await lockConn.OpenAsync(ct).ConfigureAwait(false);

            var resource = "CGNXT_OUTBOX_" + id.ToString("N");
            if (!await TryAcquireAppLockAsync(lockConn, resource, ct).ConfigureAwait(false))
                continue;

            try
            {
                var did = await ProcessSingleAsync(id, ct).ConfigureAwait(false);
                if (did) processed++;
            }
            finally
            {
                try { await ReleaseAppLockAsync(lockConn, resource, ct).ConfigureAwait(false); } catch { /* ignore */ }
            }
        }
    }

    private async Task<Guid[]> ListOutboxCandidateWorkflowIdsAsync(int take, CancellationToken ct)
    {
        if (take <= 0) return Array.Empty<Guid>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // NOTE: schema check happens elsewhere (app already uses these tables).
        const string sql = @"
SELECT TOP (@Take)
    w.WorkflowId
FROM dbo.CGGiftWorkflows w
INNER JOIN dbo.CGGiftWorkflowGifts g ON g.WorkflowId = w.WorkflowId
WHERE g.ApiAttemptedAtUtc IS NOT NULL
  AND g.ApiSucceeded = 0
ORDER BY g.ApiAttemptedAtUtc ASC, w.CreatedAtUtc ASC;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@Take", SqlDbType.Int) { Value = take });

        var list = new System.Collections.Generic.List<Guid>(Math.Min(take, 200));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            if (r[0] is Guid id)
                list.Add(id);
        }
        return list.ToArray();
    }

    private async Task<bool> ProcessSingleAsync(Guid workflowId, CancellationToken ct)
    {
        var json = await _workflowStore.GetWorkflowContextJsonAsync(workflowId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        GiftWorkflowContext? ctx;
        try
        {
            ctx = JsonSerializer.Deserialize<GiftWorkflowContext>(json);
        }
        catch
        {
            // If it can't be deserialized, do not loop forever.
            return false;
        }

        if (ctx is null)
            return false;

        // Demo workflows are never eligible for posting.
        if (ctx.IsDemo)
            return false;

        // Already succeeded.
        if (ctx.Api.Success && !string.IsNullOrWhiteSpace(ctx.Api.GiftId))
            return false;

        // Suppression is persisted via StatusTrail.
        if (IsSuppressed(ctx))
            return false;

        var now = DateTime.UtcNow;

        // Backoff is persisted via StatusTrail: compute next attempt time.
        var attemptCount = GetAttemptCount(ctx);
        var nextAttemptUtc = GetNextAttemptUtc(ctx, attemptCount);
        if (nextAttemptUtc.HasValue && nextAttemptUtc.Value > now)
            return false;

        var maxAttempts = ReadInt("Outbox.MaxAttempts", 8, min: 1, max: 100);
        if (attemptCount >= maxAttempts)
        {
            var reason = $"Max retry attempts reached ({maxAttempts}).";
            ctx.AddTrail("OutboxSuppressed", reason);
            ctx.Api.ErrorMessage = reason;
            ctx.Status = WorkflowStatus.ApiFailed;
            ctx.CompletedAtUtc = now;
            await SaveApiResultDirectAsync(ctx, ct).ConfigureAwait(false);
            return true;
        }

        if (string.IsNullOrWhiteSpace(ctx.Api.RequestJson))
        {
            var reason = "No Api.RequestJson stored; cannot retry automatically.";
            ctx.AddTrail("OutboxSuppressed", reason);
            ctx.Api.ErrorMessage = reason;
            ctx.Status = WorkflowStatus.ApiFailed;
            ctx.CompletedAtUtc = now;
            await SaveApiResultDirectAsync(ctx, ct).ConfigureAwait(false);
            return true;
        }

        RenxtGiftServer.CreatePledgeRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<RenxtGiftServer.CreatePledgeRequest>(ctx.Api.RequestJson!);
        }
        catch
        {
            var reason = "Api.RequestJson could not be parsed as CreatePledgeRequest.";
            ctx.AddTrail("OutboxSuppressed", reason);
            ctx.Api.ErrorMessage = reason;
            ctx.Status = WorkflowStatus.ApiFailed;
            ctx.CompletedAtUtc = now;
            await SaveApiResultDirectAsync(ctx, ct).ConfigureAwait(false);
            return true;
        }

        if (req is null)
            return false;

        // Record attempt up-front.
        ctx.Api.Attempted = true;
        ctx.Api.AttemptedAtUtc = now;
        ctx.Status = WorkflowStatus.ReadyToSubmit;
        ctx.AddTrail("OutboxRetryAttempt", $"Attempt {attemptCount + 1}");

        // Duplicate safety check (best-effort): if we detect a likely match, pause auto-retry.
        try
        {
            var pledgeDate = req.PledgeDate.Date;
            var startUtc = DateTime.SpecifyKind(pledgeDate.AddDays(-7), DateTimeKind.Utc);
            var endUtc = DateTime.SpecifyKind(pledgeDate.AddDays(2).AddHours(23).AddMinutes(59).AddSeconds(59), DateTimeKind.Utc);

            // NOTE: SearchGiftsAsync signatures have changed over time in this project.
            // We invoke it via reflection to remain compatible across patch versions.
            var hits = await SearchGiftsAnyAsync(
                req.ConstituentId,
                startUtc,
                endUtc,
                req.Amount,
                ct).ConfigureAwait(false);

            var matches = hits
                .Where(h => IsAmountMatch(h, req.Amount, 0.01m))
                .Take(10)
                .ToList();

            if (matches.Count > 0)
            {
                var note = "Potential duplicates in SKY: " + string.Join(", ", matches.Select(DescribeHit));
                var reason = "Duplicate safety check found one or more matching gifts in SKY. Manual review required.";

                ctx.AddTrail("OutboxDuplicateDetected", note);
                ctx.AddTrail("OutboxSuppressed", reason);
                ctx.Api.ErrorMessage = reason;
                ctx.Status = WorkflowStatus.ApiFailed;
                ctx.CompletedAtUtc = DateTime.UtcNow;
                await SaveApiResultDirectAsync(ctx, ct).ConfigureAwait(false);
                return true;
            }
        }
        catch (Exception ex)
        {
            // Best-effort: allow retry if duplicate check is unavailable.
            ctx.AddTrail("OutboxDuplicateCheckUnavailable", ex.Message);
        }

        try
        {
            var result = await _giftServer.CreatePledgeAsync(req, ct).ConfigureAwait(false);

            ctx.Api.Success = true;
            ctx.Api.GiftId = result.GiftId;
            ctx.Api.CreateResponseJson = result.RawCreateResponseJson;
            ctx.Api.InstallmentListJson = result.RawInstallmentListJson;
            ctx.Api.InstallmentAddJson = result.RawInstallmentAddJson;
            ctx.Api.ErrorMessage = null;

            ctx.Status = WorkflowStatus.ApiSucceeded;
            ctx.CompletedAtUtc = DateTime.UtcNow;
            ctx.AddTrail("OutboxApiSucceeded", "GiftId: " + result.GiftId);

            // Apply local-only matching gifts (best-effort).
            try
            {
                var match = await _giftMatch.ApplyMatchesForGiftAsync(ctx, ct).ConfigureAwait(false);
                if (match.TotalMatched > 0m)
                    ctx.AddTrail("OutboxMatchesApplied", $"TotalMatched={match.TotalMatched.ToString("F2", CultureInfo.InvariantCulture)}");
                if (match.Warnings.Length > 0)
                    ctx.AddTrail("OutboxMatchWarning", match.Warnings[0]);
            }
            catch (Exception mex)
            {
                ctx.AddTrail("OutboxMatchError", mex.Message);
                try { _ = ErrorLogger.Log(mex, "OutboxQueueProcessor.Match"); } catch { /* ignore */ }
            }

            await SaveApiResultDirectAsync(ctx, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            ctx.Api.Success = false;
            ctx.Api.GiftId = null;
            ctx.Api.ErrorMessage = ex.Message;
            ctx.Status = WorkflowStatus.ApiFailed;
            ctx.CompletedAtUtc = DateTime.UtcNow;
            ctx.AddTrail("OutboxApiFailed", ex.Message);

            await SaveApiResultDirectAsync(ctx, ct).ConfigureAwait(false);

            try { _ = ErrorLogger.Log(ex, "OutboxQueueProcessor.Retry"); } catch { /* ignore */ }
            return true;
        }
    }

    private static bool IsSuppressed(GiftWorkflowContext ctx)
    {
        var trail = ctx.StatusTrail;
        if (trail is null || trail.Count == 0) return false;

        // Once suppressed, we require manual intervention.
        return trail.Any(e => string.Equals(e.Event, "OutboxSuppressed", StringComparison.Ordinal)
                           || string.Equals(e.Event, "OutboxDuplicateDetected", StringComparison.Ordinal));
    }

    private static int GetAttemptCount(GiftWorkflowContext ctx)
    {
        var trail = ctx.StatusTrail;
        if (trail is null || trail.Count == 0) return 0;
        return trail.Count(e => string.Equals(e.Event, "OutboxRetryAttempt", StringComparison.Ordinal));
    }

    private DateTime? GetNextAttemptUtc(GiftWorkflowContext ctx, int attemptCount)
    {
        // attemptCount is number of outbox retry attempts already performed.
        if (attemptCount <= 0)
        {
            // Use the original API attempted time (if any) as the anchor for first retry backoff.
            if (ctx.Api.AttemptedAtUtc.HasValue)
                return ctx.Api.AttemptedAtUtc.Value.Add(ComputeBackoff(0));
            return null;
        }

        var last = ctx.StatusTrail?
            .Where(e => string.Equals(e.Event, "OutboxRetryAttempt", StringComparison.Ordinal))
            .Select(e => (DateTime?)e.AtUtc)
            .LastOrDefault();

        if (last is null) return null;

        return last.Value.Add(ComputeBackoff(attemptCount));
    }

    private TimeSpan ComputeBackoff(int attemptCount)
    {
        // attemptCount is 0-based here: 0 => base, 1 => base*2, 2 => base*4, ...
        var baseSeconds = ReadInt("Outbox.BaseBackoffSeconds", 60, min: 5, max: 3600);
        var maxSeconds = ReadInt("Outbox.MaxBackoffSeconds", 1800, min: 30, max: 24 * 3600);

        double seconds = baseSeconds * Math.Pow(2, Math.Max(0, attemptCount));
        seconds = Math.Min(seconds, maxSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task SaveApiResultDirectAsync(GiftWorkflowContext ctx, CancellationToken ct)
    {
        // Update only workflow Status/CompletedAtUtc/ContextJson + gift Api* columns.
        // Does NOT modify sponsorship reservations or other child tables.
        var ctxJson = JsonSerializer.Serialize(ctx);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        try
        {
            const string wfSql = @"
UPDATE dbo.CGGiftWorkflows
SET CompletedAtUtc = @CompletedAtUtc,
    Status = @Status,
    ContextJson = @ContextJson
WHERE WorkflowId = @WorkflowId;";

            await using (var cmd = new SqlCommand(wfSql, conn, tx))
            {
                cmd.Parameters.Add(new SqlParameter("@WorkflowId", SqlDbType.UniqueIdentifier) { Value = ctx.WorkflowId });
                cmd.Parameters.Add(new SqlParameter("@CompletedAtUtc", SqlDbType.DateTime2) { Value = (object?)ctx.CompletedAtUtc ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@Status", SqlDbType.NVarChar, 30) { Value = ctx.Status.ToString() });
                cmd.Parameters.Add(new SqlParameter("@ContextJson", SqlDbType.NVarChar, -1) { Value = (object?)ctxJson ?? DBNull.Value });
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            const string giftSql = @"
UPDATE dbo.CGGiftWorkflowGifts
SET ApiAttemptedAtUtc = @ApiAttemptedAtUtc,
    ApiSucceeded = @ApiSucceeded,
    ApiGiftId = @ApiGiftId,
    ApiErrorMessage = @ApiErrorMessage
WHERE WorkflowId = @WorkflowId;";

            await using (var cmd = new SqlCommand(giftSql, conn, tx))
            {
                cmd.Parameters.Add(new SqlParameter("@WorkflowId", SqlDbType.UniqueIdentifier) { Value = ctx.WorkflowId });
                cmd.Parameters.Add(new SqlParameter("@ApiAttemptedAtUtc", SqlDbType.DateTime2) { Value = (object?)ctx.Api.AttemptedAtUtc ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@ApiSucceeded", SqlDbType.Bit) { Value = ctx.Api.Success });
                cmd.Parameters.Add(new SqlParameter("@ApiGiftId", SqlDbType.NVarChar, 50) { Value = (object?)ctx.Api.GiftId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@ApiErrorMessage", SqlDbType.NVarChar, 2000) { Value = (object?)ctx.Api.ErrorMessage ?? DBNull.Value });
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<bool> TryAcquireAppLockAsync(SqlConnection conn, string resource, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("sp_getapplock", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        cmd.Parameters.AddWithValue("@Resource", resource);
        cmd.Parameters.AddWithValue("@LockMode", "Exclusive");
        cmd.Parameters.AddWithValue("@LockOwner", "Session");
        cmd.Parameters.AddWithValue("@LockTimeout", 0);

        var returnParam = cmd.Parameters.Add("@RETURN_VALUE", SqlDbType.Int);
        returnParam.Direction = ParameterDirection.ReturnValue;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        var rc = (int)returnParam.Value;
        return rc >= 0;
    }

    private static async Task ReleaseAppLockAsync(SqlConnection conn, string resource, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("sp_releaseapplock", conn)
        {
            CommandType = CommandType.StoredProcedure
        };
        cmd.Parameters.AddWithValue("@Resource", resource);
        cmd.Parameters.AddWithValue("@LockOwner", "Session");
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static int ReadInt(string key, int defaultValue, int min, int max)
    {
        try
        {
            var raw = ConfigurationManager.AppSettings[key];
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                v = defaultValue;
            if (v < min) v = min;
            if (v > max) v = max;
            return v;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static bool ReadBool(string key, bool defaultValue)
    {
        try
        {
            var raw = ConfigurationManager.AppSettings[key];
            if (string.IsNullOrWhiteSpace(raw))
                return defaultValue;
            return raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                || raw.Trim().Equals("1", StringComparison.OrdinalIgnoreCase)
                || raw.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return defaultValue;
        }
    }

    private async Task<IReadOnlyList<object>> SearchGiftsAnyAsync(
        string constituentId,
        DateTime startGiftDateUtc,
        DateTime endGiftDateUtc,
        decimal targetAmount,
        CancellationToken ct)
    {
        // SearchGiftsAsync has had multiple shapes over the life of this repo.
        // We probe for a compatible overload at runtime and fall back to "no hits".
        try
        {
            var methods = _giftServer
                .GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => string.Equals(m.Name, "SearchGiftsAsync", StringComparison.Ordinal))
                .ToList();

            // Preferred: (string, DateTime, DateTime, decimal?, decimal?, int, CancellationToken)
            var m7 = methods.FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length == 7
                       && p[0].ParameterType == typeof(string)
                       && p[1].ParameterType == typeof(DateTime)
                       && p[2].ParameterType == typeof(DateTime)
                       && (p[3].ParameterType == typeof(decimal?) || p[3].ParameterType == typeof(decimal))
                       && (p[4].ParameterType == typeof(decimal?) || p[4].ParameterType == typeof(decimal))
                       && p[5].ParameterType == typeof(int)
                       && p[6].ParameterType == typeof(CancellationToken);
            });

            if (m7 is not null)
            {
                var minAmt = targetAmount - 0.01m;
                var maxAmt = targetAmount + 0.01m;
                return await InvokeSearchAsync(_giftServer, m7, new object?[]
                {
                    constituentId,
                    startGiftDateUtc,
                    endGiftDateUtc,
                    minAmt,
                    maxAmt,
                    50,
                    ct
                }).ConfigureAwait(false);
            }

            // Next: (string, DateTime, DateTime, int, CancellationToken)
            var m5 = methods.FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length == 5
                       && p[0].ParameterType == typeof(string)
                       && p[1].ParameterType == typeof(DateTime)
                       && p[2].ParameterType == typeof(DateTime)
                       && p[3].ParameterType == typeof(int)
                       && p[4].ParameterType == typeof(CancellationToken);
            });

            if (m5 is not null)
            {
                return await InvokeSearchAsync(_giftServer, m5, new object?[]
                {
                    constituentId,
                    startGiftDateUtc,
                    endGiftDateUtc,
                    50,
                    ct
                }).ConfigureAwait(false);
            }

            // Next: (string, DateTime, DateTime, CancellationToken)
            var m4 = methods.FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length == 4
                       && p[0].ParameterType == typeof(string)
                       && p[1].ParameterType == typeof(DateTime)
                       && p[2].ParameterType == typeof(DateTime)
                       && p[3].ParameterType == typeof(CancellationToken);
            });

            if (m4 is not null)
            {
                return await InvokeSearchAsync(_giftServer, m4, new object?[]
                {
                    constituentId,
                    startGiftDateUtc,
                    endGiftDateUtc,
                    ct
                }).ConfigureAwait(false);
            }

            // Finally: (string, DateTime, DateTime)
            var m3 = methods.FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length == 3
                       && p[0].ParameterType == typeof(string)
                       && p[1].ParameterType == typeof(DateTime)
                       && p[2].ParameterType == typeof(DateTime);
            });

            if (m3 is not null)
            {
                return await InvokeSearchAsync(_giftServer, m3, new object?[]
                {
                    constituentId,
                    startGiftDateUtc,
                    endGiftDateUtc
                }).ConfigureAwait(false);
            }

            return Array.Empty<object>();
        }
        catch
        {
            return Array.Empty<object>();
        }
    }

    private static async Task<IReadOnlyList<object>> InvokeSearchAsync(object target, MethodInfo method, object?[] args)
    {
        var invoked = method.Invoke(target, args);

        if (invoked is not Task task)
            return Array.Empty<object>();

        await task.ConfigureAwait(false);

        var resultProp = task.GetType().GetProperty("Result");
        var result = resultProp?.GetValue(task);
        if (result is null) return Array.Empty<object>();

        if (result is IEnumerable enumerable)
        {
            var list = new List<object>();
            foreach (var item in enumerable)
                if (item is not null)
                    list.Add(item);
            return list;
        }

        return Array.Empty<object>();
    }

    private static bool IsAmountMatch(object? hit, decimal targetAmount, decimal tolerance)
    {
        // If we can't read an amount from the hit, be conservative and treat it as a match.
        var amt = TryGetDecimal(hit, "Amount") ?? TryGetDecimal(hit, "GiftAmount");
        if (!amt.HasValue) return true;

        var diff = amt.Value - targetAmount;
        if (diff < 0m) diff = -diff;
        return diff <= tolerance;
    }

    private static string DescribeHit(object? hit)
    {
        if (hit is null) return "(null)";

        var id = TryGetString(hit, "GiftId") ?? TryGetString(hit, "Id") ?? TryGetString(hit, "id");
        var dt = TryGetDate(hit, "GiftDate") ?? TryGetDate(hit, "Date") ?? TryGetDate(hit, "GiftDateUtc");
        var amt = TryGetDecimal(hit, "Amount") ?? TryGetDecimal(hit, "GiftAmount");

        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(id)) parts.Add(id);
        if (dt.HasValue) parts.Add(dt.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (amt.HasValue) parts.Add(amt.Value.ToString("F2", CultureInfo.InvariantCulture));

        return parts.Count > 0 ? string.Join(" | ", parts) : (hit.ToString() ?? "(unknown)");
    }

    private static string? TryGetString(object hit, string propertyName)
    {
        try
        {
            var p = hit.GetType().GetProperty(propertyName);
            var v = p?.GetValue(hit);
            if (v is null) return null;
            var s = v.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? TryGetDate(object? hit, string propertyName)
    {
        if (hit is null) return null;
        try
        {
            var p = hit.GetType().GetProperty(propertyName);
            var v = p?.GetValue(hit);
            if (v is null) return null;

            if (v is DateTime dt) return dt;
            if (v is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static decimal? TryGetDecimal(object? hit, string propertyName)
    {
        if (hit is null) return null;
        try
        {
            var p = hit.GetType().GetProperty(propertyName);
            var v = p?.GetValue(hit);
            if (v is null) return null;

            if (v is decimal d) return d;
            if (v is double dbl) return Convert.ToDecimal(dbl, CultureInfo.InvariantCulture);
            if (v is float flt) return Convert.ToDecimal(flt, CultureInfo.InvariantCulture);
            if (v is int i) return i;
            if (v is long l) return l;

            if (v is string s && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                return parsed;

            return null;
        }
        catch
        {
            return null;
        }
    }
}
