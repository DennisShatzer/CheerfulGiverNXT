using CheerfulGiverNXT.Data;
using CheerfulGiverNXT.Services;
using CheerfulGiverNXT.Workflow;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CheerfulGiverNXT.Infrastructure.Logging;

namespace CheerfulGiverNXT.ViewModels;

public sealed class LocalTransactionsViewModel : INotifyPropertyChanged
{
    private readonly IGiftWorkflowStore _store;
    private readonly RenxtGiftServer _gifts;
    private readonly IGiftMatchService _giftMatch;
    private CancellationTokenSource? _detailCts;

    public LocalTransactionsViewModel(IGiftWorkflowStore store, RenxtGiftServer gifts, IGiftMatchService giftMatch)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gifts = gifts ?? throw new ArgumentNullException(nameof(gifts));
        _giftMatch = giftMatch ?? throw new ArgumentNullException(nameof(giftMatch));

        OutcomeOptions = new ObservableCollection<string>
        {
            "All",
            "Succeeded (API)",
            "Failed (API)",
            "Not submitted",
            "Committed",
            "Commit failed"
        };

        // Default range: last 30 days through today (local time)
        var today = DateTime.Today;
        FromDate = today.AddDays(-30);
        ToDate = today;

        SelectedOutcome = OutcomeOptions[0];
        TakeText = "500";
        StatusText = "Ready.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<LocalTransactionRow> Transactions { get; } = new();

    public ObservableCollection<string> OutcomeOptions { get; }

    private LocalTransactionRow? _selectedTransaction;
    public LocalTransactionRow? SelectedTransaction
    {
        get => _selectedTransaction;
        set
        {
            _selectedTransaction = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRetrySelected));
            OnPropertyChanged(nameof(CanDeleteSelected));
            _ = LoadSelectedDetailsAsync();
        }
    }

    public bool CanRetrySelected =>
        !IsBusy
        && SelectedTransaction is not null
        && SelectedTransaction.ApiSucceeded != true;

    public bool CanDeleteSelected
    {
        get
        {
            if (IsBusy || SelectedTransaction is null)
                return false;

            // First-time delete: always allowed.
            if (SelectedTransaction.IsDeleted != true)
                return true;

            // Already deleted locally: allow re-attempt only if SKY deletion hasn't succeeded and there is a gift id.
            if (string.IsNullOrWhiteSpace(SelectedTransaction.ApiGiftId))
                return false;

            return SelectedTransaction.ApiDeleteSucceeded != true;
        }
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; OnPropertyChanged(); }
    }

    private DateTime? _fromDate;
    public DateTime? FromDate
    {
        get => _fromDate;
        set { _fromDate = value?.Date; OnPropertyChanged(); }
    }

    private DateTime? _toDate;
    public DateTime? ToDate
    {
        get => _toDate;
        set { _toDate = value?.Date; OnPropertyChanged(); }
    }

    private string _selectedOutcome = "All";
    public string SelectedOutcome
    {
        get => _selectedOutcome;
        set { _selectedOutcome = value; OnPropertyChanged(); }
    }

    private string _takeText = "500";
    public string TakeText
    {
        get => _takeText;
        set { _takeText = value; OnPropertyChanged(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotBusy)); OnPropertyChanged(nameof(CanRetrySelected)); OnPropertyChanged(nameof(CanDeleteSelected)); }
    }

    public bool IsNotBusy => !IsBusy;

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    // Outbox queue status (background auto-retry)
    public OutboxQueueStatus OutboxStatus => OutboxQueueStatus.Instance;

    // Details (selected row)
    private string _contextJson = "";
    public string ContextJson
    {
        get => _contextJson;
        private set { _contextJson = value; OnPropertyChanged(); }
    }

    private string _apiRequestJson = "";
    public string ApiRequestJson
    {
        get => _apiRequestJson;
        private set { _apiRequestJson = value; OnPropertyChanged(); }
    }

    private string _apiCreateResponseJson = "";
    public string ApiCreateResponseJson
    {
        get => _apiCreateResponseJson;
        private set { _apiCreateResponseJson = value; OnPropertyChanged(); }
    }

    private string _installmentListJson = "";
    public string InstallmentListJson
    {
        get => _installmentListJson;
        private set { _installmentListJson = value; OnPropertyChanged(); }
    }

    private string _installmentAddJson = "";
    public string InstallmentAddJson
    {
        get => _installmentAddJson;
        private set { _installmentAddJson = value; OnPropertyChanged(); }
    }

    private string _apiError = "";
    public string ApiError
    {
        get => _apiError;
        private set { _apiError = value; OnPropertyChanged(); }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var take = ParseTakeOrDefault(TakeText, 500);

            // Inclusive range on dates in local time; convert by passing local DateTime values.
            var fromLocal = (FromDate ?? DateTime.Today.AddDays(-30)).Date;
            var toLocalExclusive = (ToDate ?? DateTime.Today).Date.AddDays(1);

            string? status = null;
            bool? apiAttempted = null;
            bool? apiSucceeded = null;

            switch ((SelectedOutcome ?? "All").Trim())
            {
                case "Succeeded (API)":
                    apiAttempted = true;
                    apiSucceeded = true;
                    break;

                case "Failed (API)":
                    apiAttempted = true;
                    apiSucceeded = false;
                    break;

                case "Not submitted":
                    apiAttempted = false;
                    break;

                case "Committed":
                    status = WorkflowStatus.Committed.ToString();
                    break;

                case "Commit failed":
                    status = WorkflowStatus.CommitFailed.ToString();
                    break;

                default:
                    break;
            }

            var q = new LocalTransactionQuery(
                FromUtc: fromLocal,
                ToUtc: toLocalExclusive,
                Search: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                Status: status,
                ApiAttempted: apiAttempted,
                ApiSucceeded: apiSucceeded,
                Take: take);

            // NOTE: This ViewModel is bound to WPF controls. Do not use ConfigureAwait(false)
            // here, otherwise collection/property updates may occur off the Dispatcher thread.
            var items = await _store.ListLocalTransactionsAsync(q, ct);

            Transactions.Clear();
            foreach (var it in items)
            {
                Transactions.Add(new LocalTransactionRow
                {
                    WorkflowId = it.WorkflowId,
                    CreatedAtUtc = it.CreatedAtUtc,
                    CompletedAtUtc = it.CompletedAtUtc,
                    Status = it.Status,
                    ConstituentId = it.ConstituentId,
                    ConstituentName = it.ConstituentName ?? "",
                    Amount = it.Amount,
                    Frequency = it.Frequency ?? "",
                    PledgeDate = it.PledgeDate,
                    ApiAttemptedAtUtc = it.ApiAttemptedAtUtc,
                    ApiSucceeded = it.ApiSucceeded,
                    ApiGiftId = it.ApiGiftId ?? "",
                    ApiErrorMessage = it.ApiErrorMessage ?? "",
                    SponsoredDate = it.SponsoredDate,
                    Slot = it.Slot ?? "",
                    MachineName = it.MachineName,
                    WindowsUser = it.WindowsUser,
                    IsFirstTimeGiver = it.IsFirstTimeGiver,
                    IsNewRadioConstituent = it.IsNewRadioConstituent,

                    IsDeleted = it.IsDeleted,
                    DeletedAtUtc = it.DeletedAtUtc,
                    DeletedByUser = it.DeletedByUser ?? "",

                    ApiDeleteAttemptedAtUtc = it.ApiDeleteAttemptedAtUtc,
                    ApiDeleteSucceeded = it.ApiDeleteSucceeded,
                    ApiDeleteErrorMessage = it.ApiDeleteErrorMessage ?? ""
                });
            }

            StatusText = $"Loaded {Transactions.Count} transaction(s).";
        }
        catch (Exception ex)
        {
            StatusText = "Error: " + ex.Message;
            try { _ = ErrorLogger.Log(ex, "LocalTransactionsViewModel.RefreshAsync"); } catch { }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public sealed record SkyDuplicateCheckResult(
        bool HasDuplicates,
        string? WarningText,
        string? ErrorText
    );

    /// <summary>
    /// Best-effort duplicate safety check against SKY before retrying.
    /// Returns a warning if similar gifts exist for the constituent in a small gift_date window.
    /// </summary>
    public async Task<SkyDuplicateCheckResult> CheckSkyDuplicatesForSelectedAsync(CancellationToken ct = default)
    {
        if (SelectedTransaction is null)
            return new SkyDuplicateCheckResult(false, null, null);

        try
        {
            var json = await _store.GetWorkflowContextJsonAsync(SelectedTransaction.WorkflowId, ct);
            if (string.IsNullOrWhiteSpace(json))
                return new SkyDuplicateCheckResult(false, null, "No stored ContextJson found.");

            var ctx = JsonSerializer.Deserialize<GiftWorkflowContext>(json, CaseInsensitiveJson);
            if (ctx is null)
                return new SkyDuplicateCheckResult(false, null, "Unable to deserialize stored ContextJson.");

            if (string.IsNullOrWhiteSpace(ctx.Api.RequestJson))
                return new SkyDuplicateCheckResult(false, null, "No stored Api.RequestJson found.");

            var req = JsonSerializer.Deserialize<RenxtGiftServer.CreatePledgeRequest>(ctx.Api.RequestJson!, CaseInsensitiveJson);
            if (req is null)
                return new SkyDuplicateCheckResult(false, null, "Unable to deserialize stored Api.RequestJson.");

            // Small window around pledge date to catch accidental duplicates.
            var from = req.PledgeDate.Date.AddDays(-7);
            var to = req.PledgeDate.Date.AddDays(2);

            var gifts = await _gifts.SearchGiftsAsync(req.ConstituentId, from, to, limit: 50, ct);

            // Compare by amount (within a cent) and (if present) gift_type == pledge.
            var candidates = gifts
                .Where(g => g.Amount.HasValue && Math.Abs(g.Amount.Value - req.Amount) <= 0.01m)
                .Where(g => g.GiftDate is null || (g.GiftDate.Value.Date >= from && g.GiftDate.Value.Date <= to))
                .Where(g => string.IsNullOrWhiteSpace(g.GiftType) || string.Equals(g.GiftType, "Pledge", StringComparison.OrdinalIgnoreCase))
                .Take(10)
                .ToList();

            if (candidates.Count == 0)
                return new SkyDuplicateCheckResult(false, null, null);

            var sb = new StringBuilder();
            sb.AppendLine("Potential duplicate(s) found in SKY:");
            sb.AppendLine($"Constituent: {req.ConstituentId}  Amount: {req.Amount:0.00}  Window: {from:yyyy-MM-dd} to {to:yyyy-MM-dd}");
            sb.AppendLine();
            foreach (var c in candidates)
            {
                var dateTxt = c.GiftDate?.ToString("yyyy-MM-dd") ?? "(no date)";
                var typeTxt = string.IsNullOrWhiteSpace(c.GiftType) ? "(type?)" : c.GiftType;
                var amtTxt = c.Amount.HasValue ? c.Amount.Value.ToString("0.00", CultureInfo.InvariantCulture) : "(no amount)";
                sb.AppendLine($"• {dateTxt}  {typeTxt}  {amtTxt}  Id={c.Id}");
            }

            return new SkyDuplicateCheckResult(true, sb.ToString().Trim(), null);
        }
        catch (Exception ex)
        {
            try { _ = ErrorLogger.Log(ex, "LocalTransactionsViewModel.CheckSkyDuplicatesForSelectedAsync"); } catch { }
            // Do not block retry if the check cannot run.
            return new SkyDuplicateCheckResult(false, null, ex.Message);
        }
    }

    public async Task<int> ExportCurrentTransactionsToCsvAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentNullException(nameof(filePath));

        // Snapshot current rows (avoid iterating UI collection while writing).
        var rows = Transactions.ToList();

        static string Esc(string? s)
        {
            s ??= "";
            var needs = s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
            if (!needs) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        var sb = new StringBuilder();
        sb.AppendLine("CreatedUtc,Status,ConstituentId,Name,Amount,Frequency,PledgeDate,SponsoredDate,Slot,ApiOk,ApiGiftId,Machine,User,FirstTimeGiver,NewRadioConstituent,Deleted,DeletedAtUtc,DeletedByUser,SkyDeleteOk,SkyDeleteAttemptedAtUtc,SkyDeleteError,ApiError");

        foreach (var r in rows)
        {
            ct.ThrowIfCancellationRequested();

            sb.Append(Esc(r.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Esc(r.Status)).Append(',');
            sb.Append(Esc(r.ConstituentId.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Esc(r.ConstituentName)).Append(',');
            sb.Append(Esc(r.Amount.HasValue ? r.Amount.Value.ToString("0.00", CultureInfo.InvariantCulture) : "")).Append(',');
            sb.Append(Esc(r.Frequency)).Append(',');
            sb.Append(Esc(r.PledgeDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "")).Append(',');
            sb.Append(Esc(r.SponsoredDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "")).Append(',');
            sb.Append(Esc(r.Slot)).Append(',');
            sb.Append(Esc(r.ApiSucceededText)).Append(',');
            sb.Append(Esc(r.ApiGiftId)).Append(',');
            sb.Append(Esc(r.MachineName)).Append(',');
            sb.Append(Esc(r.WindowsUser)).Append(',');
            sb.Append(Esc(r.IsFirstTimeGiver?.ToString() ?? "")).Append(',');
            sb.Append(Esc(r.IsNewRadioConstituent?.ToString() ?? "")).Append(',');
            sb.Append(Esc(r.IsDeleted?.ToString() ?? "")).Append(',');
            sb.Append(Esc(r.DeletedAtUtc?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "")).Append(',');
            sb.Append(Esc(r.DeletedByUser)).Append(',');
            sb.Append(Esc(r.ApiDeleteSucceeded?.ToString() ?? "")).Append(',');
            sb.Append(Esc(r.ApiDeleteAttemptedAtUtc?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "")).Append(',');
            sb.Append(Esc(r.ApiDeleteErrorMessage)).Append(',');
            sb.Append(Esc(r.ApiErrorMessage));
            sb.AppendLine();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);

        // Write UTF-8 BOM for Excel friendliness.
        var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await File.WriteAllTextAsync(filePath, sb.ToString(), utf8Bom, ct);

        return rows.Count;
    }

    public async Task RetrySelectedAsync(CancellationToken ct = default)
    {
        if (!CanRetrySelected || SelectedTransaction is null)
            return;

        var workflowId = SelectedTransaction.WorkflowId;

        IsBusy = true;
        try
        {
            StatusText = "Loading transaction context...";

            var json = await _store.GetWorkflowContextJsonAsync(workflowId, ct);
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("No stored ContextJson found for this workflow.");

            GiftWorkflowContext ctx;
            try
            {
                ctx = JsonSerializer.Deserialize<GiftWorkflowContext>(json, CaseInsensitiveJson)
                      ?? throw new InvalidOperationException("Unable to deserialize stored workflow context.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Unable to deserialize stored workflow context.", ex);
            }

            if (ctx.Api.Success && !string.IsNullOrWhiteSpace(ctx.Api.GiftId))
                throw new InvalidOperationException("This workflow already has a successful SKY submission (API Gift Id present)." );

            if (string.IsNullOrWhiteSpace(ctx.Api.RequestJson))
                throw new InvalidOperationException("This workflow has no stored API RequestJson to retry.");

            RenxtGiftServer.CreatePledgeRequest req;
            try
            {
                req = JsonSerializer.Deserialize<RenxtGiftServer.CreatePledgeRequest>(ctx.Api.RequestJson!, CaseInsensitiveJson)
                      ?? throw new InvalidOperationException("Unable to deserialize stored API RequestJson.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Unable to deserialize stored API RequestJson.", ex);
            }

            var user = Environment.UserName;
            var machine = Environment.MachineName;

            ctx.AddTrail("RetryRequested", $"Requested by {user} on {machine}.");

            // Preserve prior error in the trail before overwriting.
            if (!string.IsNullOrWhiteSpace(ctx.Api.ErrorMessage))
                ctx.AddTrail("PriorApiError", ctx.Api.ErrorMessage);

            ctx.Api.Attempted = true;
            ctx.Api.AttemptedAtUtc = DateTime.UtcNow;
            ctx.Api.ErrorMessage = null;

            StatusText = "Retrying submit to SKY API...";
            ctx.AddTrail("RetryApiAttempted", $"AttemptedAtUtc={ctx.Api.AttemptedAtUtc:O}");

            try
            {
                var result = await _gifts.CreatePledgeAsync(req, ct);

                ctx.Api.Success = true;
                ctx.Api.GiftId = result.GiftId;
                ctx.Api.CreateResponseJson = result.RawCreateResponseJson;
                ctx.Api.InstallmentListJson = result.RawInstallmentListJson;
                ctx.Api.InstallmentAddJson = result.RawInstallmentAddJson;

                ctx.Status = WorkflowStatus.ApiSucceeded;
                ctx.AddTrail("RetryApiSucceeded", $"GiftId={result.GiftId}");

                // Apply matching gifts (best-effort). This is local-only.
                try
                {
                    var match = await _giftMatch.ApplyMatchesForGiftAsync(ctx, ct);
                    if (match.TotalMatched > 0m)
                        ctx.AddTrail("RetryMatchApplied", $"TotalMatched={match.TotalMatched:0.00}");
                    if (match.Warnings.Length > 0)
                        ctx.AddTrail("RetryMatchWarning", match.Warnings[0]);
                }
                catch (Exception mex)
                {
                    ctx.AddTrail("RetryMatchError", mex.Message);
                    try { _ = ErrorLogger.Log(mex, "LocalTransactionsViewModel.RetrySelectedAsync.Match"); } catch { }
                }
            }
            catch (Exception ex)
            {
                ctx.Api.Success = false;
                ctx.Api.GiftId = null;
                ctx.Api.CreateResponseJson = null;
                ctx.Api.InstallmentListJson = null;
                ctx.Api.InstallmentAddJson = null;
                ctx.Api.ErrorMessage = ex.Message;
                ctx.Status = WorkflowStatus.ApiFailed;
                ctx.AddTrail("RetryApiFailed", ex.Message);
                try { _ = ErrorLogger.Log(ex, "LocalTransactionsViewModel.RetrySelectedAsync.Api"); } catch { }
            }

            // Re-commit locally (updates gifts row + sponsorship reservation association).
            ctx.CompletedAtUtc = DateTime.UtcNow;
            ctx.AddTrail("RetryLocalCommitAttempted");

            try
            {
                await _store.SaveAsync(ctx, ct);

                ctx.AddTrail("RetryLocalCommitSucceeded");
                await _store.SaveWorkflowOnlyAsync(ctx, ct);
            }
            catch (Exception ex)
            {
                ctx.AddTrail("RetryLocalCommitFailed", ex.Message);
                try { _ = ErrorLogger.Log(ex, "LocalTransactionsViewModel.RetrySelectedAsync.LocalCommit"); } catch { }

                // Best-effort: if the child save failed, still try to update the workflow ContextJson.
                try { await _store.SaveWorkflowOnlyAsync(ctx, ct); } catch { /* ignore */ }

                throw;
            }

            // Refresh list and keep selection.
            await RefreshAsync(ct);
            SelectedTransaction = Transactions.FirstOrDefault(t => t.WorkflowId == workflowId);

            StatusText = ctx.Api.Success && !string.IsNullOrWhiteSpace(ctx.Api.GiftId)
                ? $"Retry succeeded. API Gift Id: {ctx.Api.GiftId}"
                : "Retry completed (API did not succeed).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Admin-only operation: soft-delete the selected pledge locally and attempt to delete it in SKY API.
    /// Always records an audit snapshot in dbo.CGDeletedPledges.
    /// </summary>
    public async Task DeleteSelectedAsync(CancellationToken ct = default)
    {
        if (!CanDeleteSelected || SelectedTransaction is null)
            return;

        var workflowId = SelectedTransaction.WorkflowId;
        var apiGiftId = (SelectedTransaction.ApiGiftId ?? string.Empty).Trim();

        IsBusy = true;
        try
        {
            var deletedAtUtc = DateTime.UtcNow;
            var mark = new DeletedPledgeMark(
                DeletedAtUtc: deletedAtUtc,
                DeletedByMachine: Environment.MachineName,
                DeletedByUser: Environment.UserName,
                Reason: "Deleted via LocalTransactions"
            );

            StatusText = "Marking local record as deleted...";
            await _store.MarkWorkflowGiftDeletedLocalAsync(workflowId, mark, ct);

            if (string.IsNullOrWhiteSpace(apiGiftId))
            {
                await RefreshAsync(ct);
                SelectedTransaction = Transactions.FirstOrDefault(t => t.WorkflowId == workflowId);
                StatusText = "Deleted locally (no SKY Gift Id present).";
                return;
            }

            var attemptAtUtc = DateTime.UtcNow;
            try
            {
                StatusText = "Deleting gift in SKY API...";
                await _gifts.DeleteGiftAsync(apiGiftId, ct);

                await _store.UpdateWorkflowGiftSkyDeleteAsync(workflowId, new SkyDeleteResult(attemptAtUtc, true, null), ct);

                await RefreshAsync(ct);
                SelectedTransaction = Transactions.FirstOrDefault(t => t.WorkflowId == workflowId);
                StatusText = $"Deleted locally and in SKY API. Gift Id: {apiGiftId}";
            }
            catch (Exception ex)
            {
                await _store.UpdateWorkflowGiftSkyDeleteAsync(workflowId, new SkyDeleteResult(attemptAtUtc, false, ex.Message), ct);

                await RefreshAsync(ct);
                SelectedTransaction = Transactions.FirstOrDefault(t => t.WorkflowId == workflowId);
                StatusText = "Deleted locally, but SKY delete failed: " + ex.Message;
                try { _ = ErrorLogger.Log(ex, "LocalTransactionsViewModel.DeleteSelectedAsync.SkyDelete"); } catch { }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadSelectedDetailsAsync()
    {
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = new CancellationTokenSource();
        var ct = _detailCts.Token;

        ContextJson = "";
        ApiRequestJson = "";
        ApiCreateResponseJson = "";
        InstallmentListJson = "";
        InstallmentAddJson = "";
        ApiError = "";

        if (SelectedTransaction is null)
            return;

        try
        {
            // Keep continuation on the Dispatcher thread for safe UI-bound updates.
            var json = await _store.GetWorkflowContextJsonAsync(SelectedTransaction.WorkflowId, ct);
            if (string.IsNullOrWhiteSpace(json))
                return;

            ContextJson = PrettyJson(json);

            GiftWorkflowContext? ctx = null;
            try
            {
                ctx = JsonSerializer.Deserialize<GiftWorkflowContext>(json);
            }
            catch
            {
                // ignore; keep raw context
            }

            if (ctx is not null)
            {
                ApiRequestJson = PrettyJson(ctx.Api.RequestJson);
                ApiCreateResponseJson = PrettyJson(ctx.Api.CreateResponseJson);
                InstallmentListJson = PrettyJson(ctx.Api.InstallmentListJson);
                InstallmentAddJson = PrettyJson(ctx.Api.InstallmentAddJson);

                ApiError = string.IsNullOrWhiteSpace(ctx.Api.ErrorMessage) ? "" : ctx.Api.ErrorMessage!;
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            ApiError = "Error loading details: " + ex.Message;
            try { _ = ErrorLogger.Log(ex, "LocalTransactionsViewModel.LoadSelectedDetailsAsync"); } catch { }
        }
    }

    private static string PrettyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "";

        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json.Trim();
        }
    }

    private static int ParseTakeOrDefault(string? text, int def)
    {
        if (int.TryParse((text ?? "").Trim(), out var v) && v > 0)
            return Math.Min(v, 5000);

        return def;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class LocalTransactionRow
{
    public Guid WorkflowId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public string Status { get; set; } = "";

    public int ConstituentId { get; set; }
    public string ConstituentName { get; set; } = "";

    public decimal? Amount { get; set; }
    public string Frequency { get; set; } = "";
    public DateTime? PledgeDate { get; set; }

    public DateTime? ApiAttemptedAtUtc { get; set; }
    public bool? ApiSucceeded { get; set; }
    public string ApiGiftId { get; set; } = "";
    public string ApiErrorMessage { get; set; } = "";

    public DateTime? SponsoredDate { get; set; }
    public string Slot { get; set; } = "";

    public string MachineName { get; set; } = "";
    public string WindowsUser { get; set; } = "";

    public bool? IsFirstTimeGiver { get; set; }
    public bool? IsNewRadioConstituent { get; set; }

    // Soft-delete fields (LocalTransactions admin)
    public bool? IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string DeletedByUser { get; set; } = "";

    // SKY delete audit (best-effort)
    public DateTime? ApiDeleteAttemptedAtUtc { get; set; }
    public bool? ApiDeleteSucceeded { get; set; }
    public string ApiDeleteErrorMessage { get; set; } = "";

    public string ApiSucceededText => ApiSucceeded is null ? "" : (ApiSucceeded.Value ? "Yes" : "No");
    public string IsDeletedText => IsDeleted == true ? "Yes" : "";
    public string SkyDeleteOkText => ApiDeleteSucceeded is null ? "" : (ApiDeleteSucceeded.Value ? "Yes" : "No");

    public string CreatedUtcText => CreatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss");
    public string? PledgeDateText => PledgeDate?.ToString("yyyy-MM-dd");
    public string? SponsoredDateText => SponsoredDate?.ToString("yyyy-MM-dd");

    public string? DeletedAtUtcText => DeletedAtUtc?.ToString("yyyy-MM-dd HH:mm:ss");
    public string? SkyDeleteAttemptedAtUtcText => ApiDeleteAttemptedAtUtc?.ToString("yyyy-MM-dd HH:mm:ss");

    public string AmountText => Amount.HasValue ? Amount.Value.ToString("C") : "";
}
