using CheerfulGiverNXT.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.ViewModels;

public sealed class CampaignsAdminViewModel : INotifyPropertyChanged
{
    private readonly SqlCampaignsRepository _repo;

    public CampaignsAdminViewModel(SqlCampaignsRepository repo)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<CampaignRow> Rows { get; } = new();

    private CampaignRow? _selectedRow;
    public CampaignRow? SelectedRow
    {
        get => _selectedRow;
        set { _selectedRow = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRemove)); }
    }

    public bool CanRemove => SelectedRow is not null;

    private readonly HashSet<int> _deletedIds = new();

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
            Rows.Clear();
            _deletedIds.Clear();

            var list = await _repo.ListAsync(ct);
            foreach (var r in list)
                Rows.Add(r);

            StatusText = "Loaded.";
        }
        catch (Exception ex)
        {
            StatusText = "Error: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void AddRow()
    {
        var tz = Rows.FirstOrDefault()?.TimeZoneId;
        if (string.IsNullOrWhiteSpace(tz)) tz = "America/New_York";

        var today = DateTime.Today;

        var row = new CampaignRow
        {
            CampaignRecordId = 0,
            CampaignName = "",
            StartLocal = today,
            EndLocalExclusive = today.AddDays(1),
            TimeZoneId = tz!,
            GoalAmount = null,
            GoalFirstTimeGivers = null,
            IsActive = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        Rows.Add(row);
        SelectedRow = row;
        StatusText = "New row added.";
    }

    public void RemoveSelected()
    {
        if (SelectedRow is null) return;

        if (SelectedRow.CampaignRecordId > 0)
            _deletedIds.Add(SelectedRow.CampaignRecordId);

        Rows.Remove(SelectedRow);
        SelectedRow = Rows.LastOrDefault();
        StatusText = "Removed (pending save).";
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        // Normalize and skip blank names.
        var normalized = Rows
            .Select(r =>
            {
                r.CampaignName = (r.CampaignName ?? "").Trim();
                r.TimeZoneId = (r.TimeZoneId ?? "").Trim();
                return r;
            })
            .Where(r => !string.IsNullOrWhiteSpace(r.CampaignName))
            .ToArray();

        IsBusy = true;
        try
        {
            await _repo.SaveAsync(normalized, _deletedIds.ToArray(), username: Environment.UserName, ct: ct);

            // Reload to pick up identity values + audit timestamps.
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
