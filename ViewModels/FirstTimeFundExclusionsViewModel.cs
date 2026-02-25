using CheerfulGiverNXT.Data;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CheerfulGiverNXT.Infrastructure.Logging;

namespace CheerfulGiverNXT.ViewModels;

public sealed class FirstTimeFundExclusionsViewModel : INotifyPropertyChanged
{
    private readonly SqlFirstTimeFundExclusionsRepository _repo;
    private readonly ICampaignContext _campaignContext;

    public FirstTimeFundExclusionsViewModel(SqlFirstTimeFundExclusionsRepository repo, ICampaignContext campaignContext)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _campaignContext = campaignContext ?? throw new ArgumentNullException(nameof(campaignContext));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FirstTimeFundExclusionRow> Rows { get; } = new();

    private FirstTimeFundExclusionRow? _selectedRow;
    public FirstTimeFundExclusionRow? SelectedRow
    {
        get => _selectedRow;
        set { _selectedRow = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRemove)); }
    }

    private int? _campaignRecordId;
    public int? CampaignRecordId
    {
        get => _campaignRecordId;
        private set { _campaignRecordId = value; OnPropertyChanged(); OnPropertyChanged(nameof(CampaignLabel)); }
    }

    private string _campaignName = "";

    public string CampaignLabel
    {
        get
        {
            if (!CampaignRecordId.HasValue) return "Campaign: (unknown)";
            var name = (_campaignName ?? "").Trim();
            return string.IsNullOrWhiteSpace(name)
                ? $"CampaignRecordId: {CampaignRecordId.Value}"
                : $"{name} (CampaignRecordId: {CampaignRecordId.Value})";
        }
    }

    public bool CanRemove => SelectedRow is not null;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotBusy)); }
    }
    public bool IsNotBusy => !IsBusy;

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            // This ViewModel is UI-bound (ObservableCollection + property notifications).
            // Keep continuations on the WPF Dispatcher thread to avoid cross-thread collection updates.
            CampaignRecordId = await _campaignContext.GetCurrentCampaignRecordIdAsync(ct);

            Rows.Clear();

            if (!CampaignRecordId.HasValue || CampaignRecordId.Value <= 0)
            {
                _campaignName = "";
                StatusText = "No campaign configured.";
                OnPropertyChanged(nameof(CampaignLabel));
                return;
            }

            _campaignName = await _repo.TryGetCampaignNameAsync(CampaignRecordId.Value, ct) ?? "";
            OnPropertyChanged(nameof(CampaignLabel));

            var list = await _repo.ListAsync(CampaignRecordId.Value, ct);
            foreach (var r in list)
                Rows.Add(r);

            StatusText = "Loaded.";
        }
        catch (Exception ex)
        {
            StatusText = "Error: " + ex.Message;
            try { _ = ErrorLogger.Log(ex, "FirstTimeFundExclusionsViewModel"); } catch { }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void AddRow()
    {
        Rows.Add(new FirstTimeFundExclusionRow { FundName = "", IsActive = true, SortOrder = null, CreatedAt = null });
        SelectedRow = Rows.LastOrDefault();
    }

    public void RemoveSelected()
    {
        if (SelectedRow is null) return;
        Rows.Remove(SelectedRow);
        SelectedRow = Rows.LastOrDefault();
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (!CampaignRecordId.HasValue || CampaignRecordId.Value <= 0)
            throw new InvalidOperationException("No campaign is configured.");

        var normalized = Rows
            .Select(r => new FirstTimeFundExclusionRow
            {
                FundName = (r.FundName ?? "").Trim(),
                IsActive = r.IsActive,
                SortOrder = r.SortOrder,
                CreatedAt = r.CreatedAt
            })
            .Where(r => !string.IsNullOrWhiteSpace(r.FundName))
            .ToArray();

        IsBusy = true;
        try
        {
            await _repo.ReplaceAllAsync(CampaignRecordId.Value, normalized, ct);

            // Reload so CreatedAt reflects persisted rows
            await RefreshAsync(ct);

            StatusText = "Saved.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
