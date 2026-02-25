using CheerfulGiverNXT.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CheerfulGiverNXT.Infrastructure.Logging;

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
            try { _ = ErrorLogger.Log(ex, "CampaignsAdminViewModel"); } catch { }
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
            FundList = "",
            AnonymousGiverId = null,
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
                r.FundList = (r.FundList ?? "").Trim();

                // AnonymousGiverId is optional. Treat <= 0 as NULL.
                if (r.AnonymousGiverId.HasValue && r.AnonymousGiverId.Value <= 0)
                    r.AnonymousGiverId = null;

                NormalizeSponsorshipThresholds(r);
                return r;
            })
            .Where(r => !string.IsNullOrWhiteSpace(r.CampaignName))
            .ToArray();

        // Workflow improvement:
        // dbo.CGCampaigns is the single source of truth, and the rest of the app prefers a single "active" row.
        // If an operator marks multiple campaigns Active, keep only ONE active on save:
        //   - Prefer the currently selected row (if it is active)
        //   - Otherwise, keep the most recent campaign (by CampaignRecordId, then StartLocal)
        var active = normalized.Where(r => r.IsActive).ToList();
        if (active.Count > 1)
        {
            CampaignRow? keep = null;

            if (SelectedRow is not null && SelectedRow.IsActive)
            {
                // Try to locate the same instance in the normalized set.
                keep = normalized.FirstOrDefault(r => ReferenceEquals(r, SelectedRow))
                       ?? normalized.FirstOrDefault(r => r.CampaignRecordId > 0 && r.CampaignRecordId == SelectedRow.CampaignRecordId);
            }

            keep ??= active
                .OrderByDescending(r => r.CampaignRecordId)
                .ThenByDescending(r => r.StartLocal)
                .FirstOrDefault();

            foreach (var r in active)
            {
                if (!ReferenceEquals(r, keep))
                    r.IsActive = false;
            }

            StatusText = "Note: Multiple Active campaigns were selected. Only one will remain Active after saving.";
        }

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

    private static void NormalizeSponsorshipThresholds(CampaignRow r)
    {
        const decimal DefaultHalf = 1000m;
        const decimal DefaultFull = 2000m;

        if (r.SponsorshipHalfDayAmount <= 0m) r.SponsorshipHalfDayAmount = DefaultHalf;
        if (r.SponsorshipFullDayAmount <= 0m) r.SponsorshipFullDayAmount = DefaultFull;
        if (r.SponsorshipFullDayAmount < r.SponsorshipHalfDayAmount)
            r.SponsorshipFullDayAmount = Math.Max(r.SponsorshipHalfDayAmount, DefaultFull);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
