using CheerfulGiverNXT.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CheerfulGiverNXT.Infrastructure.Logging;

namespace CheerfulGiverNXT.ViewModels;

public sealed class GiftMatchAdminViewModel : INotifyPropertyChanged
{
    private readonly IGiftMatchService _svc;

    public GiftMatchAdminViewModel(IGiftMatchService svc)
    {
        _svc = svc ?? throw new ArgumentNullException(nameof(svc));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<MatchChallengeAdminRow> Challenges { get; } = new();

    private int? _campaignRecordId;
    public int? CampaignRecordId
    {
        get => _campaignRecordId;
        private set { _campaignRecordId = value; OnPropertyChanged(); OnPropertyChanged(nameof(CampaignLabel)); }
    }

    public string CampaignLabel => CampaignRecordId.HasValue
        ? $"CampaignRecordId: {CampaignRecordId.Value}"
        : "CampaignRecordId: (unknown)";

    private string _anonymousConstituentIdText = "";
    public string AnonymousConstituentIdText
    {
        get => _anonymousConstituentIdText;
        set { _anonymousConstituentIdText = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSaveAnonymousId)); }
    }

    public bool CanSaveAnonymousId => int.TryParse((AnonymousConstituentIdText ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0;

    private string _challengeName = "";
    public string ChallengeName
    {
        get => _challengeName;
        set { _challengeName = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanCreateChallenge)); }
    }

    private string _challengeBudgetText = "";
    public string ChallengeBudgetText
    {
        get => _challengeBudgetText;
        set { _challengeBudgetText = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanCreateChallenge)); }
    }

    public bool CanCreateChallenge
    {
        get
        {
            if (string.IsNullOrWhiteSpace((ChallengeName ?? "").Trim())) return false;
            return decimal.TryParse((ChallengeBudgetText ?? "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var b) && b > 0m;
        }
    }

    private MatchChallengeAdminRow? _selectedChallenge;
    public MatchChallengeAdminRow? SelectedChallenge
    {
        get => _selectedChallenge;
        set { _selectedChallenge = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanDeactivateSelected)); }
    }

    public bool CanDeactivateSelected => SelectedChallenge is not null && SelectedChallenge.IsActive;

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
            var snap = await _svc.GetAdminSnapshotAsync(ct).ConfigureAwait(false);
            CampaignRecordId = snap.CampaignRecordId;

            AnonymousConstituentIdText = snap.AnonymousMatchConstituentId?.ToString(CultureInfo.InvariantCulture) ?? "";

            Challenges.Clear();
            foreach (var c in snap.Challenges)
                Challenges.Add(c);

            StatusText = "Loaded.";
        }
        catch (Exception ex)
        {
            StatusText = "Error: " + ex.Message;
            try { _ = ErrorLogger.Log(ex, "GiftMatchAdminViewModel"); } catch { }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveAnonymousIdAsync(CancellationToken ct = default)
    {
        if (!CanSaveAnonymousId) return;
        var id = int.Parse(AnonymousConstituentIdText.Trim(), CultureInfo.InvariantCulture);

        IsBusy = true;
        try
        {
            await _svc.SetAnonymousMatchConstituentIdAsync(id, ct).ConfigureAwait(false);
            StatusText = "Anonymous match constituent id saved.";
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync(ct).ConfigureAwait(false);
    }

    public async Task CreateChallengeAsync(CancellationToken ct = default)
    {
        if (!CanCreateChallenge) return;

        var name = (ChallengeName ?? "").Trim();
        var budget = decimal.Parse((ChallengeBudgetText ?? "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture);

        IsBusy = true;
        try
        {
            await _svc.CreateMatchChallengeAsync(name, budget, ct).ConfigureAwait(false);
            StatusText = "Challenge created.";

            ChallengeName = "";
            ChallengeBudgetText = "";
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync(ct).ConfigureAwait(false);
    }

    public async Task DeactivateSelectedAsync(CancellationToken ct = default)
    {
        if (SelectedChallenge is null) return;

        IsBusy = true;
        try
        {
            await _svc.DeactivateChallengeAsync(SelectedChallenge.ChallengeRecordId, ct).ConfigureAwait(false);
            StatusText = "Challenge deactivated.";
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync(ct).ConfigureAwait(false);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
