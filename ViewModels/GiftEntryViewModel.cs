// GiftEntryViewModel.cs
// Pledge entry screen VM (pledge commitments only) with a friendly “One-time” option.
// Adds: a single workflow/transaction object as the source of truth.
// On Save: attempts SKY API call then ALWAYS commits workflow to SQL Express (success or failure).

using CheerfulGiverNXT.Data;
using CheerfulGiverNXT.Infrastructure;
using CheerfulGiverNXT.Services;
using CheerfulGiverNXT.Workflow;
using Microsoft.Data.SqlClient;
using System.Configuration;
using System.Data;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CheerfulGiverNXT.Infrastructure.Logging;
using CheerfulGiverNXT.Infrastructure.AppMode;

namespace CheerfulGiverNXT.ViewModels
{
    public sealed class GiftEntryViewModel : INotifyPropertyChanged
    {
        public sealed record FrequencyPreset(
            string Display,
            PledgeFrequency ApiFrequency,
            int DefaultInstallments,
            bool LockInstallments,
            bool LockStartDateToPledgeDate
        );

        public sealed record SponsorshipOption(string Display, decimal ThresholdAmount);

        public FrequencyPreset[] FrequencyPresets { get; } =
        {
            // “One-time” UX: still uses Monthly in API (frequency value is required),
            // but forces number_of_installments = 1 and start_date = pledge_date.
            new("One-time",  PledgeFrequency.Monthly,   1,  true,  true),
            new("Monthly",   PledgeFrequency.Monthly,  12,  false, false),
            new("Quarterly", PledgeFrequency.Quarterly, 4,  false, false),
            new("Annually",  PledgeFrequency.Annually,  1,  false, false),
        };


        private const decimal DefaultHalfDayThresholdAmount = 1000m;
        private const decimal DefaultFullDayThresholdAmount = 2000m;

        // These are loaded per campaign from dbo.CGCampaigns (with safe defaults if the columns don't exist).
        private decimal _halfDayThresholdAmount = DefaultHalfDayThresholdAmount;
        private decimal _fullDayThresholdAmount = DefaultFullDayThresholdAmount;

        // Cache sponsorship option arrays so selection + stored ThresholdAmount stay consistent with the campaign.
        private SponsorshipOption[] _halfDaySponsorshipOptions =
        {
            new("Half-day AM", DefaultHalfDayThresholdAmount),
            new("Half-day PM", DefaultHalfDayThresholdAmount),
        };

        private SponsorshipOption[] _fullDaySponsorshipOptions =
        {
            new("Full day", DefaultFullDayThresholdAmount),
            new("Half-day AM", DefaultHalfDayThresholdAmount),
            new("Half-day PM", DefaultHalfDayThresholdAmount),
        };


        private readonly RenxtConstituentLookupService.ConstituentGridRow _row;
        private readonly RenxtGiftServer _gifts;

        // NEW: server-side SKY transaction queue (client no longer posts gifts directly)
        private readonly ISkyTransactionQueue _skyQueue;

        // NEW: workflow source of truth + SQL store
        private readonly GiftWorkflowContext _workflow;
        private readonly IGiftWorkflowStore _workflowStore;

        // NEW: campaign context (CampaignRecordId source of truth)
        private readonly ICampaignContext _campaignContext;
        private int? _campaignRecordIdFromContext;

        // NEW: gift match challenges
        private readonly IGiftMatchService _giftMatch;

        // Match banner state (oldest active challenge is shown; matches are applied oldest-first)
        private bool _showMatchBanner;
        public bool ShowMatchBanner
        {
            get => _showMatchBanner;
            private set { _showMatchBanner = value; OnPropertyChanged(); }
        }

        private string _activeMatchChallengeName = "";
        public string ActiveMatchChallengeName
        {
            get => _activeMatchChallengeName;
            private set { _activeMatchChallengeName = value; OnPropertyChanged(); OnPropertyChanged(nameof(MatchBannerTitle)); }
        }

        private int _otherActiveMatchChallengeCount;
        public int OtherActiveMatchChallengeCount
        {
            get => _otherActiveMatchChallengeCount;
            private set
            {
                _otherActiveMatchChallengeCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasMultipleActiveMatchChallenges));
                OnPropertyChanged(nameof(MatchBannerTitle));
            }
        }

        public bool HasMultipleActiveMatchChallenges => OtherActiveMatchChallengeCount > 0;

        public string MatchBannerTitle
        {
            get
            {
                if (string.IsNullOrWhiteSpace(ActiveMatchChallengeName)) return "Matching challenge active";
                if (OtherActiveMatchChallengeCount <= 0) return $"Matching challenge: {ActiveMatchChallengeName}";
                return $"Matching challenge: {ActiveMatchChallengeName} (+{OtherActiveMatchChallengeCount} more)";
            }
        }

        private decimal _activeMatchBudget;
        public decimal ActiveMatchBudget
        {
            get => _activeMatchBudget;
            private set { _activeMatchBudget = value; OnPropertyChanged(); }
        }

        private decimal _activeMatchUsed;
        public decimal ActiveMatchUsed
        {
            get => _activeMatchUsed;
            private set { _activeMatchUsed = value; OnPropertyChanged(); }
        }

        private decimal _activeMatchRemaining;
        public decimal ActiveMatchRemaining
        {
            get => _activeMatchRemaining;
            private set { _activeMatchRemaining = value; OnPropertyChanged(); }
        }

        private decimal _totalMatchRemaining;
        public decimal TotalMatchRemaining
        {
            get => _totalMatchRemaining;
            private set { _totalMatchRemaining = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? RequestClose;

        public GiftWorkflowContext Workflow => _workflow;


        public ObservableCollection<GiftHistoryItem> ExistingCampaignGifts { get; } = new();


        // Dates that are fully booked (FULL, or both AM and PM) for the current campaign.
        public ObservableCollection<DateTime> FullyBookedSponsorshipDates { get; } = new();

        private string[] _reservedDayParts = Array.Empty<string>();
        private int _reservedDayPartsLoadVersion;

        private string _sponsorshipAvailabilityStatus = "";
        public string SponsorshipAvailabilityStatus
        {
            get => _sponsorshipAvailabilityStatus;
            private set { _sponsorshipAvailabilityStatus = value; OnPropertyChanged(); }
        }

        private bool _hasAvailableSponsorshipSlots = true;
        public bool HasAvailableSponsorshipSlots
        {
            get => _hasAvailableSponsorshipSlots;
            private set { _hasAvailableSponsorshipSlots = value; OnPropertyChanged(); }
        }

        private int _fullyBookedLoadVersion;

        private int? GetCampaignRecordIdForSponsorship()
        {
            if (int.TryParse((CampaignIdText ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cid) && cid > 0)
                return cid;

            if (int.TryParse((_workflow.Gift.CampaignId ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out cid) && cid > 0)
                return cid;

            if (_campaignRecordIdFromContext.HasValue && _campaignRecordIdFromContext.Value > 0)
                return _campaignRecordIdFromContext.Value;

            return null;
        }

        private async Task InitializeCampaignFromContextAsync()
        {
            try
            {
                // If a campaign id is already present (e.g., set by upstream code), don't overwrite.
                var existing = string.IsNullOrWhiteSpace(CampaignIdText)
                    ? (_workflow.Gift.CampaignId ?? "")
                    : CampaignIdText;

                if (int.TryParse((existing ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var already) && already > 0)
                {
                    _campaignRecordIdFromContext = already;
                    // Normalize: keep the VM and workflow aligned.
                    if (string.IsNullOrWhiteSpace(CampaignIdText))
                        CampaignIdText = already.ToString(CultureInfo.InvariantCulture);
                    if (string.IsNullOrWhiteSpace(_workflow.Gift.CampaignId))
                        _workflow.Gift.CampaignId = already.ToString(CultureInfo.InvariantCulture);
                    return;
                }

                var id = await _campaignContext.GetCurrentCampaignRecordIdAsync().ConfigureAwait(true);
                if (!id.HasValue || id.Value <= 0) return;

                _campaignRecordIdFromContext = id.Value;

                // Set CampaignIdText (hidden) so:
                // - SKY API pledge uses campaign_id
                // - gift history and sponsorship lookups are campaign-scoped
                CampaignIdText = id.Value.ToString(CultureInfo.InvariantCulture);
                _workflow.Gift.CampaignId = CampaignIdText;
            }
            catch
            {
                // Best-effort; campaign may be optional.
            }
        }

        private async Task ReloadFullyBookedSponsorshipDatesAsync()
        {
            var version = Interlocked.Increment(ref _fullyBookedLoadVersion);

            try
            {
                var campaignRecordId = GetCampaignRecordIdForSponsorship();
                var start = DateTime.Today.Date;
                var end = start.AddMonths(18).Date;

                var dates = await _workflowStore
                    .ListFullyBookedSponsorshipDatesAsync(campaignRecordId, start, end)
                    .ConfigureAwait(true);

                if (version != _fullyBookedLoadVersion) return;

                FullyBookedSponsorshipDates.Clear();
                foreach (var d in dates)
                    FullyBookedSponsorshipDates.Add(d.Date);
            }
            catch
            {
                if (version != _fullyBookedLoadVersion) return;
                FullyBookedSponsorshipDates.Clear();
            }
        }

        private async Task ReloadReservedDayPartsForSelectedDateAsync()
        {
            var version = Interlocked.Increment(ref _reservedDayPartsLoadVersion);

            try
            {
                if (!SponsorshipDate.HasValue)
                {
                    if (version != _reservedDayPartsLoadVersion) return;
                    _reservedDayParts = Array.Empty<string>();
                    ApplySponsorshipOptionAvailability();
                    return;
                }

                var campaignRecordId = GetCampaignRecordIdForSponsorship();
                var parts = await _workflowStore
                    .ListReservedDayPartsAsync(campaignRecordId, SponsorshipDate.Value.Date)
                    .ConfigureAwait(true);

                if (version != _reservedDayPartsLoadVersion) return;

                _reservedDayParts = parts ?? Array.Empty<string>();
                ApplySponsorshipOptionAvailability();
            }
            catch
            {
                if (version != _reservedDayPartsLoadVersion) return;
                _reservedDayParts = Array.Empty<string>();
                ApplySponsorshipOptionAvailability();
            }
        }

        private bool _isLoadingExistingCampaignGifts;
        public bool IsLoadingExistingCampaignGifts
        {
            get => _isLoadingExistingCampaignGifts;
            private set
            {
                if (_isLoadingExistingCampaignGifts == value) return;
                _isLoadingExistingCampaignGifts = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExistingCampaignGiftsHeader));
            }
        }

        private string _existingCampaignGiftsStatus = "";
        public string ExistingCampaignGiftsStatus
        {
            get => _existingCampaignGiftsStatus;
            private set { _existingCampaignGiftsStatus = value; OnPropertyChanged(); }
        }



        private string _campaignNameForHeader = "";
        private int _campaignNameLoadVersion;


        private int _sponsorshipThresholdLoadVersion;

        private static void NormalizeThresholds(ref decimal half, ref decimal full)
        {
            if (half <= 0m) half = DefaultHalfDayThresholdAmount;
            if (full <= 0m) full = DefaultFullDayThresholdAmount;
            if (full < half) full = Math.Max(half, DefaultFullDayThresholdAmount);
        }

        private void UpdateSponsorshipOptionsCache()
        {
            var half = _halfDayThresholdAmount;
            var full = _fullDayThresholdAmount;

            _halfDaySponsorshipOptions = new[]
            {
                new SponsorshipOption("Half-day AM", half),
                new SponsorshipOption("Half-day PM", half)
            };

            _fullDaySponsorshipOptions = new[]
            {
                new SponsorshipOption("Full day", full),
                new SponsorshipOption("Half-day AM", half),
                new SponsorshipOption("Half-day PM", half)
            };
        }

        private void ResetSponsorshipThresholdsToDefaults()
        {
            _halfDayThresholdAmount = DefaultHalfDayThresholdAmount;
            _fullDayThresholdAmount = DefaultFullDayThresholdAmount;
            UpdateSponsorshipOptionsCache();
            RefreshSponsorshipEligibility();
        }

        private async Task LoadSponsorshipThresholdsAsync(int campaignRecordId)
        {
            var version = Interlocked.Increment(ref _sponsorshipThresholdLoadVersion);

            var half = DefaultHalfDayThresholdAmount;
            var full = DefaultFullDayThresholdAmount;

            try
            {
                var cs = ConfigurationManager.ConnectionStrings["CheerfulGiver"]?.ConnectionString;
                if (!string.IsNullOrWhiteSpace(cs))
                {
                    await using var conn = new SqlConnection(cs);
                    await conn.OpenAsync().ConfigureAwait(true);

                    const string colSql = @"
SELECT
    CASE WHEN COL_LENGTH('dbo.CGCampaigns', 'SponsorshipHalfDayAmount') IS NOT NULL THEN 1 ELSE 0 END AS HasHalf,
    CASE WHEN COL_LENGTH('dbo.CGCampaigns', 'SponsorshipFullDayAmount') IS NOT NULL THEN 1 ELSE 0 END AS HasFull;";

                    bool hasCols = false;
                    await using (var cmdCols = new SqlCommand(colSql, conn))
                    await using (var rdrCols = await cmdCols.ExecuteReaderAsync().ConfigureAwait(true))
                    {
                        if (await rdrCols.ReadAsync().ConfigureAwait(true))
                        {
                            var hasHalf = !rdrCols.IsDBNull(0) && rdrCols.GetInt32(0) == 1;
                            var hasFull = !rdrCols.IsDBNull(1) && rdrCols.GetInt32(1) == 1;
                            hasCols = hasHalf && hasFull;
                        }
                    }

                    if (hasCols)
                    {
                        const string sql = @"
SELECT TOP (1)
    SponsorshipHalfDayAmount,
    SponsorshipFullDayAmount
FROM dbo.CGCampaigns
WHERE CampaignRecordId = @Id;";

                        await using var cmd = new SqlCommand(sql, conn);
                        cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = campaignRecordId });

                        await using var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(true);
                        if (await rdr.ReadAsync().ConfigureAwait(true))
                        {
                            if (!rdr.IsDBNull(0)) half = rdr.GetDecimal(0);
                            if (!rdr.IsDBNull(1)) full = rdr.GetDecimal(1);
                        }
                    }
                }
            }
            catch
            {
                // Best-effort: keep defaults.
            }

            NormalizeThresholds(ref half, ref full);

            if (version != _sponsorshipThresholdLoadVersion) return;

            _halfDayThresholdAmount = half;
            _fullDayThresholdAmount = full;
            UpdateSponsorshipOptionsCache();

            RefreshSponsorshipEligibility();
        }

        private async Task LoadCampaignNameAsync(int campaignRecordId)
        {
            var version = Interlocked.Increment(ref _campaignNameLoadVersion);

            string name = "";
            try
            {
                var cs = ConfigurationManager.ConnectionStrings["CheerfulGiver"]?.ConnectionString;
                if (!string.IsNullOrWhiteSpace(cs))
                {
                    await using var conn = new SqlConnection(cs);
                    await conn.OpenAsync().ConfigureAwait(true);

                    const string sql = @"
SELECT TOP (1) CampaignName
FROM dbo.CGCampaigns
WHERE CampaignRecordId = @Id;";

                    await using var cmd = new SqlCommand(sql, conn);
                    cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = campaignRecordId });

                    var obj = await cmd.ExecuteScalarAsync().ConfigureAwait(true);
                    name = (obj is null || obj is DBNull) ? "" : (Convert.ToString(obj) ?? "").Trim();
                }
            }
            catch
            {
                name = "";
            }
            finally
            {
                if (version == _campaignNameLoadVersion)
                {
                    _campaignNameForHeader = name;
                    OnPropertyChanged(nameof(ExistingCampaignGiftsHeader));
                }
            }
        }

        public string ExistingCampaignGiftsHeader
        {
            get
            {
                int? campaignRecordId = null;
                if (int.TryParse((CampaignIdText ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cid) && cid > 0)
                    campaignRecordId = cid;

                var campaignLabel = campaignRecordId.HasValue
                    ? (!string.IsNullOrWhiteSpace(_campaignNameForHeader) ? _campaignNameForHeader : $"Campaign {campaignRecordId.Value}")
                    : "All campaigns";

                if (IsLoadingExistingCampaignGifts)
                    return $"{campaignLabel} gifts: loading...";

                return $"{campaignLabel} gifts: {ExistingCampaignGifts.Count}";
            }
        }


        private int _giftHistoryLoadVersion;

        private async Task ReloadExistingCampaignGiftsAsync()
        {
            var version = Interlocked.Increment(ref _giftHistoryLoadVersion);

            try
            {
                IsLoadingExistingCampaignGifts = true;
                ExistingCampaignGiftsStatus = "Loading prior gifts...";

                var campaignId = string.IsNullOrWhiteSpace(CampaignIdText) ? null : CampaignIdText.Trim();
                var items = await _workflowStore.ListSuccessfulGiftsAsync(ConstituentId, campaignId, take: 50).ConfigureAwait(true);

                if (version != _giftHistoryLoadVersion) return;

                ExistingCampaignGifts.Clear();
                foreach (var it in items)
                    ExistingCampaignGifts.Add(it);

                ExistingCampaignGiftsStatus = items.Length == 0 ? "No gifts found." : "";
            }
            catch (Exception ex)
            {
                if (version != _giftHistoryLoadVersion) return;
                ExistingCampaignGifts.Clear();
                ExistingCampaignGiftsStatus = "Unable to load prior gifts: " + ex.Message;
                TryAppendErrorLog(ex);
            }
            finally
            {
                if (version == _giftHistoryLoadVersion)
                    IsLoadingExistingCampaignGifts = false;
            }
        }

        public int ConstituentId => _row.Id;
        public string FullName => _row.FullName;

        public string ConstituentSummary
        {
            get
            {
                var spouse = string.IsNullOrWhiteSpace(_row.Spouse) ? "" : _row.Spouse.Trim();

                // Display-only spacing between Constituent ID and Partner for readability.
                // (Does not change any underlying property names/logic.)
                var line1 = $"Constituent ID: {_row.Id}" + (string.IsNullOrWhiteSpace(spouse) ? "" : $"    —    Partner: {spouse}");

                var street = (_row.Street ?? "").Trim();
                var city = (_row.City ?? "").Trim();
                var state = (_row.State ?? "").Trim();
                var zip = (_row.Zip ?? "").Trim();

                var line2 = street;
                var line3 = string.Join(" ", new[]
                {
                    string.Join(", ", new[] { city, state }.Where(s => !string.IsNullOrWhiteSpace(s))),
                    zip
                }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

                return string.Join("\n", new[] { line1, line2, line3 }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
        }

        private string _amountText = "";
        public string AmountText
        {
            get => _amountText;
            set
            {
                if (_amountText == value) return;
                _amountText = value;
                OnPropertyChanged();
                RefreshSponsorshipEligibility();
                RefreshCanSave();
            }
        }

        private DateTime? _pledgeDate = DateTime.Today;
        public DateTime? PledgeDate
        {
            get => _pledgeDate;
            set
            {
                _pledgeDate = value;
                OnPropertyChanged();

                if (IsStartDateLockedToPledgeDate && _pledgeDate.HasValue)
                {
                    if (FirstInstallmentDate != _pledgeDate.Value.Date)
                        FirstInstallmentDate = _pledgeDate.Value.Date;
                }

                RefreshCanSave();
            }
        }

        // Keep a sane default: start date = pledge date initially.
        private DateTime? _firstInstallmentDate = DateTime.Today;
        public DateTime? FirstInstallmentDate
        {
            get => _firstInstallmentDate;
            set
            {
                _firstInstallmentDate = value;
                OnPropertyChanged();

                if (IsStartDateLockedToPledgeDate && PledgeDate.HasValue)
                {
                    var pd = PledgeDate.Value.Date;
                    if (_firstInstallmentDate != pd)
                    {
                        _firstInstallmentDate = pd;
                        OnPropertyChanged(nameof(FirstInstallmentDate));
                    }
                }

                RefreshCanSave();
            }
        }

        private PledgeFrequency _frequency = PledgeFrequency.Monthly;
        public PledgeFrequency Frequency
        {
            get => _frequency;
            private set
            {
                _frequency = value;
                OnPropertyChanged();
            }
        }

        private string _numberOfInstallmentsText = "1";
        public string NumberOfInstallmentsText
        {
            get => _numberOfInstallmentsText;
            set
            {
                _numberOfInstallmentsText = value;
                OnPropertyChanged();
                RefreshCanSave();
            }
        }

        private bool _isInstallmentsLocked;
        public bool IsInstallmentsLocked
        {
            get => _isInstallmentsLocked;
            private set
            {
                _isInstallmentsLocked = value;
                OnPropertyChanged();
            }
        }

        private bool _isStartDateLockedToPledgeDate;
        public bool IsStartDateLockedToPledgeDate
        {
            get => _isStartDateLockedToPledgeDate;
            private set
            {
                _isStartDateLockedToPledgeDate = value;
                OnPropertyChanged();
            }
        }

        private FrequencyPreset _selectedPreset;
        public FrequencyPreset SelectedPreset
        {
            get => _selectedPreset;
            set
            {
                _selectedPreset = value;
                OnPropertyChanged();

                Frequency = value.ApiFrequency;
                NumberOfInstallmentsText = value.DefaultInstallments.ToString(CultureInfo.InvariantCulture);
                IsInstallmentsLocked = value.LockInstallments;
                IsStartDateLockedToPledgeDate = value.LockStartDateToPledgeDate;

                if (IsStartDateLockedToPledgeDate && PledgeDate.HasValue)
                    FirstInstallmentDate = PledgeDate.Value.Date;

                RefreshCanSave();
            }
        }

        // Hidden, but still used by SaveAsync
        private string _fundIdText = "86";
        public string FundIdText
        {
            get => _fundIdText;
            set
            {
                _fundIdText = value;
                OnPropertyChanged();
                RefreshCanSave();
            }
        }

        private string _campaignIdText = "";
        public string CampaignIdText
        {
            get => _campaignIdText;
            set
            {
                _campaignIdText = value;

                if (int.TryParse((_campaignIdText ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cid) && cid > 0)
                {
                    _ = LoadCampaignNameAsync(cid);
                    _ = LoadSponsorshipThresholdsAsync(cid);
                }
                else
                {
                    _campaignNameForHeader = "";
                    ResetSponsorshipThresholdsToDefaults();
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExistingCampaignGiftsHeader));
                _ = ReloadExistingCampaignGiftsAsync();
                _ = ReloadFullyBookedSponsorshipDatesAsync();
                _ = ReloadReservedDayPartsForSelectedDateAsync();
            }
        }

        private string _appealIdText = "";
        public string AppealIdText
        {
            get => _appealIdText;
            set { _appealIdText = value; OnPropertyChanged(); }
        }

        private string _packageIdText = "";
        public string PackageIdText
        {
            get => _packageIdText;
            set { _packageIdText = value; OnPropertyChanged(); }
        }

        private bool _sendReminder = false;
        public bool SendReminder
        {
            get => _sendReminder;
            set { _sendReminder = value; OnPropertyChanged(); }
        }

        private string _comments = "";
        public string Comments
        {
            get => _comments;
            set { _comments = value; OnPropertyChanged(); }
        }

        // Sponsorship eligibility (driven by AmountText thresholds)
        private SponsorshipOption[] _eligibleSponsorshipOptions = Array.Empty<SponsorshipOption>();
        public SponsorshipOption[] EligibleSponsorshipOptions
        {
            get => _eligibleSponsorshipOptions;
            private set
            {
                _eligibleSponsorshipOptions = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowSponsorshipSelector));
            }
        }

        // Sponsorship UI is shown when the gift amount qualifies (regardless of current date availability).
        public bool ShowSponsorshipSelector => ComputeEligibleSponsorshipOptionsByAmount().Length > 0;

        private SponsorshipOption? _selectedSponsorshipOption;
        public SponsorshipOption? SelectedSponsorshipOption
        {
            get => _selectedSponsorshipOption;
            set
            {
                _selectedSponsorshipOption = value;
                OnPropertyChanged();
                RefreshCanSave();
            }
        }

        private DateTime? _sponsorshipDate;
        /// <summary>
        /// The date the constituent wants to sponsor. Only used/shown when Sponsorship is eligible.
        /// </summary>
        public DateTime? SponsorshipDate
        {
            get => _sponsorshipDate;
            set
            {
                if (_sponsorshipDate == value) return;
                _sponsorshipDate = value;
                OnPropertyChanged();

                // When the date changes, reload reservations and re-filter available tiers.
                _ = ReloadReservedDayPartsForSelectedDateAsync();
            }
        }

        private string _statusText = "Ready.";
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set { _isBusy = value; OnPropertyChanged(); RefreshCanSave(); }
        }

        private bool _canSave;
        public bool CanSave
        {
            get => _canSave;
            private set
            {
                if (_canSave == value) return;
                _canSave = value;
                OnPropertyChanged();
                // During construction SaveCommand may not be initialized yet.
                _saveCommand?.RaiseCanExecuteChanged();
            }
        }

        // Backing field is required because CanSave may change during construction
        // before the command is created.
        private AsyncRelayCommand? _saveCommand;
        public AsyncRelayCommand SaveCommand => _saveCommand ??= new AsyncRelayCommand(SaveAsync, () => CanSave);

        public GiftEntryViewModel(
            RenxtConstituentLookupService.ConstituentGridRow row,
            GiftWorkflowContext workflow,
            RenxtGiftServer giftServer,
            IGiftWorkflowStore workflowStore,
            ICampaignContext campaignContext,
            IGiftMatchService giftMatchService,
            ISkyTransactionQueue skyTransactionQueue)
        {
            _row = row ?? throw new ArgumentNullException(nameof(row));
            _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
            _gifts = giftServer ?? throw new ArgumentNullException(nameof(giftServer));
            _workflowStore = workflowStore ?? throw new ArgumentNullException(nameof(workflowStore));
            _campaignContext = campaignContext ?? throw new ArgumentNullException(nameof(campaignContext));
            _giftMatch = giftMatchService ?? throw new ArgumentNullException(nameof(giftMatchService));
            _skyQueue = skyTransactionQueue ?? throw new ArgumentNullException(nameof(skyTransactionQueue));

            // Default to One-time.
            _selectedPreset = FrequencyPresets[0];
            Frequency = _selectedPreset.ApiFrequency;
            IsInstallmentsLocked = _selectedPreset.LockInstallments;
            IsStartDateLockedToPledgeDate = _selectedPreset.LockStartDateToPledgeDate;
            NumberOfInstallmentsText = _selectedPreset.DefaultInstallments.ToString(CultureInfo.InvariantCulture);

            if (IsStartDateLockedToPledgeDate && PledgeDate.HasValue)
                FirstInstallmentDate = PledgeDate.Value.Date;

            // Create command after defaults are applied.
            _saveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave);

            

            ExistingCampaignGifts.CollectionChanged += (_, __) =>
                OnPropertyChanged(nameof(ExistingCampaignGiftsHeader));

            // Load the active campaign id (best-effort) and let the CampaignIdText setter
            // trigger the dependent reloads.
            _ = InitializeCampaignFromContextAsync();

            // Load match challenge status (best-effort). This drives the "Remaining Match Budget" banner.
            _ = ReloadMatchBannerAsync();

            // Fallback initial loads (in case campaign id isn't available).
            _ = ReloadExistingCampaignGiftsAsync();
            _ = ReloadFullyBookedSponsorshipDatesAsync();
            _ = ReloadReservedDayPartsForSelectedDateAsync();
            RefreshSponsorshipEligibility();
            RefreshCanSave();
        }

        private async Task ReloadMatchBannerAsync()
        {
            try
            {
                var snap = await _giftMatch.GetAdminSnapshotAsync().ConfigureAwait(true);

                // Only show active challenges with remaining budget.
                var active = (snap.Challenges ?? Array.Empty<MatchChallengeAdminRow>())
                    .Where(c => c.IsActive && c.Remaining > 0m)
                    .OrderBy(c => c.CreatedAtUtc)
                    .ThenBy(c => c.ChallengeRecordId)
                    .ToArray();

                if (active.Length == 0)
                {
                    ShowMatchBanner = false;
                    ActiveMatchChallengeName = "";
                    OtherActiveMatchChallengeCount = 0;
                    ActiveMatchBudget = 0m;
                    ActiveMatchUsed = 0m;
                    ActiveMatchRemaining = 0m;
                    TotalMatchRemaining = 0m;
                    return;
                }

                var current = active[0];
                ActiveMatchChallengeName = current.Name ?? "";
                OtherActiveMatchChallengeCount = Math.Max(0, active.Length - 1);
                ActiveMatchBudget = current.Budget;
                ActiveMatchRemaining = current.Remaining;
                ActiveMatchUsed = Math.Max(0m, current.Budget - current.Remaining);
                TotalMatchRemaining = active.Sum(c => c.Remaining);
                ShowMatchBanner = true;
            }
            catch
            {
                // Banner is best-effort; never block gift entry.
                ShowMatchBanner = false;
            }
        }

        private SponsorshipOption[] ComputeEligibleSponsorshipOptionsByAmount()
        {
            if (!TryParseAmount(out var amt) || amt <= 0m)
                return Array.Empty<SponsorshipOption>();

            if (amt >= _fullDayThresholdAmount) return _fullDaySponsorshipOptions;
            if (amt >= _halfDayThresholdAmount) return _halfDaySponsorshipOptions;
            return Array.Empty<SponsorshipOption>();
        }

        private static string? GetDesiredDayPart(SponsorshipOption opt)
        {
            var s = (opt.Display ?? "").Trim();
            if (s.Length == 0) return null;
            if (s.IndexOf("full", StringComparison.OrdinalIgnoreCase) >= 0) return "FULL";
            if (s.IndexOf("am", StringComparison.OrdinalIgnoreCase) >= 0) return "AM";
            if (s.IndexOf("pm", StringComparison.OrdinalIgnoreCase) >= 0) return "PM";
            return null;
        }

        private bool IsDayPartAvailable(string desiredDayPart)
        {
            // FULL booked blocks everything.
            if (_reservedDayParts.Any(p => string.Equals(p, "FULL", StringComparison.OrdinalIgnoreCase)))
                return false;

            if (string.Equals(desiredDayPart, "FULL", StringComparison.OrdinalIgnoreCase))
            {
                // Full day requires neither AM nor PM to be reserved.
                return !_reservedDayParts.Any(p => string.Equals(p, "AM", StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(p, "PM", StringComparison.OrdinalIgnoreCase));
            }

            // Half-day AM/PM requires that exact day-part not be reserved.
            return !_reservedDayParts.Any(p => string.Equals(p, desiredDayPart, StringComparison.OrdinalIgnoreCase));
        }

        private void ApplySponsorshipOptionAvailability()
        {
            var baseEligible = ComputeEligibleSponsorshipOptionsByAmount();

            if (baseEligible.Length == 0)
            {
                EligibleSponsorshipOptions = Array.Empty<SponsorshipOption>();
                SelectedSponsorshipOption = null;
                SponsorshipDate = null;
                SponsorshipAvailabilityStatus = "";
                HasAvailableSponsorshipSlots = true;
                return;
            }

            // IMPORTANT UX: Do NOT auto-select a date.
            // If sponsorship is eligible but the operator hasn't chosen a date yet,
            // show the eligible tiers but require a date selection before allowing Save.
            if (!SponsorshipDate.HasValue)
            {
                EligibleSponsorshipOptions = baseEligible;
                HasAvailableSponsorshipSlots = true;

                // Keep selection if still available, else default to first.
                if (_selectedSponsorshipOption is null || !baseEligible.Any(x => x.Display == _selectedSponsorshipOption.Display))
                    SelectedSponsorshipOption = baseEligible[0];

                SponsorshipAvailabilityStatus = "Select a sponsor date to view available slots.";
                RefreshCanSave();
                return;
            }

            // If we have a selected date, filter options by what's reserved on that date.
            var filtered = baseEligible
                .Where(o =>
                {
                    var dp = GetDesiredDayPart(o);
                    if (dp is null) return true; // unknown -> don't block
                    return IsDayPartAvailable(dp);
                })
                .ToArray();

            EligibleSponsorshipOptions = filtered;

            if (filtered.Length == 0)
            {
                SelectedSponsorshipOption = null;
                HasAvailableSponsorshipSlots = false;
                SponsorshipAvailabilityStatus = "No sponsorship slots are available for the selected date.";
                RefreshCanSave();
                return;
            }

            HasAvailableSponsorshipSlots = true;

            // Keep selection if still available, else default to first.
            if (_selectedSponsorshipOption is null || !filtered.Any(x => x.Display == _selectedSponsorshipOption.Display))
                SelectedSponsorshipOption = filtered[0];

            // Friendly status.
            var avail = string.Join(", ", filtered.Select(x => x.Display));
            SponsorshipAvailabilityStatus = string.IsNullOrWhiteSpace(avail)
                ? ""
                : $"Available sponsorship slots: {avail}.";

            RefreshCanSave();
        }

        private void RefreshSponsorshipEligibility() => ApplySponsorshipOptionAvailability();

        private void RefreshCanSave()
        {
            var baseOk = !IsBusy
                         && TryParseAmount(out var amt) && amt > 0m
                         && PledgeDate.HasValue
                         && FirstInstallmentDate.HasValue
                         && TryParseInstallments(out var n) && n > 0
                         && !string.IsNullOrWhiteSpace(FundIdText);

            // If the gift amount makes sponsorship selectable, require the operator to pick
            // an available tier and date.
            bool sponsorshipOk = true;
            if (ShowSponsorshipSelector)
            {
                sponsorshipOk = SponsorshipDate.HasValue
                                && SelectedSponsorshipOption is not null
                                && EligibleSponsorshipOptions.Any(x => x.Display == SelectedSponsorshipOption.Display);
            }

            CanSave = baseOk && sponsorshipOk;
        }

        private static bool TryParseInt(string? s, out int value) =>
            int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        private bool TryParseInstallments(out int n) => TryParseInt(NumberOfInstallmentsText, out n);

        private bool TryParseAmount(out decimal amount) =>
            decimal.TryParse(AmountText, NumberStyles.Number, CultureInfo.CurrentCulture, out amount);

        private async Task SaveAsync()
        {
            // Snapshot current mode at save time (operators can toggle Demo while a workflow is open).
            _workflow.IsDemo = _workflow.IsDemo || AppModeState.Instance.IsDemo;

            // Ensure we have the current CampaignRecordId before validating sponsorship availability.
            // This prevents querying sponsorship reservations across *all* campaigns (false positives).
            if (string.IsNullOrWhiteSpace(CampaignIdText) && _campaignRecordIdFromContext is null)
                await InitializeCampaignFromContextAsync().ConfigureAwait(true);

            if (!TryParseAmount(out var amount) || amount <= 0m) { StatusText = "Enter a valid amount."; return; }
            if (!PledgeDate.HasValue) { StatusText = "Select a pledge date."; return; }
            if (!FirstInstallmentDate.HasValue) { StatusText = "Select a first installment date."; return; }
            if (!TryParseInstallments(out var installments) || installments <= 0) { StatusText = "# of installments must be a whole number."; return; }
            if (string.IsNullOrWhiteSpace(FundIdText)) { StatusText = "Fund ID is required."; return; }

            // Sponsorship slot validation (prevents saving into an already-booked date/slot).
            if (ShowSponsorshipSelector)
            {
                var campaignRecordIdForSponsorship = GetCampaignRecordIdForSponsorship();
                if (!campaignRecordIdForSponsorship.HasValue)
                {
                    StatusText = "No active campaign is configured. Set the active CampaignRecordId in your campaign configuration tool.";
                    return;
                }

                if (!SponsorshipDate.HasValue)
                {
                    StatusText = "Select a sponsorship date.";
                    return;
                }

                if (SelectedSponsorshipOption is null)
                {
                    StatusText = "Select a sponsor tier.";
                    return;
                }

                var desired = GetDesiredDayPart(SelectedSponsorshipOption);
                if (!string.IsNullOrWhiteSpace(desired))
                {
                    try
                    {
                        var reserved = await _workflowStore
                            .ListReservedDayPartsAsync(campaignRecordIdForSponsorship, SponsorshipDate.Value.Date)
                            .ConfigureAwait(true);

                        _reservedDayParts = reserved ?? Array.Empty<string>();
                        ApplySponsorshipOptionAvailability();

                        if (!IsDayPartAvailable(desired))
                        {
                            StatusText = "That sponsorship date is already booked. Please choose another date or tier.";
                            return;
                        }
                    }
                    catch
                    {
                        // If we can't verify availability, fail closed for sponsorship.
                        StatusText = "Unable to verify sponsorship availability. Try again.";
                        return;
                    }
                }
            }

            // Update workflow draft BEFORE attempting the API.
            _workflow.Gift.Amount = amount;
            _workflow.Gift.Frequency = SelectedPreset.Display; // UI-friendly label
            _workflow.Gift.Installments = installments;
            _workflow.Gift.PledgeDate = PledgeDate.Value.Date;
            _workflow.Gift.StartDate = FirstInstallmentDate.Value.Date;
            _workflow.Gift.FundId = FundIdText.Trim();
            _workflow.Gift.CampaignId = string.IsNullOrWhiteSpace(CampaignIdText) ? null : CampaignIdText.Trim();
            _workflow.Gift.AppealId = string.IsNullOrWhiteSpace(AppealIdText) ? null : AppealIdText.Trim();
            _workflow.Gift.PackageId = string.IsNullOrWhiteSpace(PackageIdText) ? null : PackageIdText.Trim();
            _workflow.Gift.SendReminder = SendReminder;
            _workflow.Gift.Comments = string.IsNullOrWhiteSpace(Comments) ? null : Comments.Trim();

            if (ShowSponsorshipSelector && SelectedSponsorshipOption is not null)
            {
                _workflow.Gift.Sponsorship.IsEnabled = true;
                _workflow.Gift.Sponsorship.Slot = SelectedSponsorshipOption.Display;
                _workflow.Gift.Sponsorship.ThresholdAmount = SelectedSponsorshipOption.ThresholdAmount;
                _workflow.Gift.Sponsorship.SponsoredDate = SponsorshipDate?.Date;
            }
            else
            {
                _workflow.Gift.Sponsorship.IsEnabled = false;
                _workflow.Gift.Sponsorship.Slot = null;
                _workflow.Gift.Sponsorship.ThresholdAmount = null;
                _workflow.Gift.Sponsorship.SponsoredDate = null;
            }

            _workflow.Status = WorkflowStatus.ReadyToSubmit;

            IsBusy = true;
            StatusText = "Preparing pledge...";

            try
            {
                var req = new RenxtGiftServer.CreatePledgeRequest(
                    ConstituentId: ConstituentId.ToString(CultureInfo.InvariantCulture),
                    Amount: amount,
                    PledgeDate: PledgeDate.Value,
                    FundId: FundIdText.Trim(),

                    Frequency: Frequency,
                    NumberOfInstallments: installments,
                    StartDate: FirstInstallmentDate.Value,

                    PaymentMethod: "Other",
                    Comments: string.IsNullOrWhiteSpace(Comments) ? null : Comments.Trim(),
                    // IMPORTANT:
                    // - CampaignIdText in this app represents the *local* CampaignRecordId (used for
                    //   sponsorship availability + local gift history scoping), NOT the RE NXT campaign system ID.
                    // - Sending it to SKY API as campaign_id can cause HTTP 400 (invalid campaign/fund/appeal/package).
                    // If you later want to assign RE NXT Campaign/Appeal/Package IDs, add separate UI/config fields.
                    CampaignId: null,
                    AppealId: null,
                    PackageId: null,

                    VerifyInstallmentsAfterCreate: true,
                    AddInstallmentsIfMissing: true
                );

                var requestJson = JsonSerializer.Serialize(req);
                _workflow.Api.RequestJson = requestJson;

                // IMPORTANT:
                // Client no longer posts gifts directly to SKY API.
                // Instead, we enqueue the JSON request into dbo.CGSKYTransactions for a server-side worker.

                // Demo mode must never enqueue server-side transactions.
                if (AppModeState.Instance.IsDemo)
                {
                    _workflow.IsDemo = true;

                    _workflow.Api.Attempted = false;
                    _workflow.Api.AttemptedAtUtc = null;
                    _workflow.Api.Success = false;
                    _workflow.Api.GiftId = null;
                    _workflow.Api.CreateResponseJson = null;
                    _workflow.Api.InstallmentListJson = null;
                    _workflow.Api.InstallmentAddJson = null;
                    _workflow.Api.ErrorMessage = "Demo mode enabled. Not queued for server submission.";

                    _workflow.AddTrail("SkyQueueSuppressed", _workflow.Api.ErrorMessage);
                    _workflow.Status = WorkflowStatus.ReadyToSubmit;

                    StatusText = "Saved (Demo). Not queued for server.";

                    RequestClose?.Invoke(this, EventArgs.Empty);
                    return;
                }

                StatusText = "Queueing pledge for server submission...";

                var queueId = await _skyQueue.EnqueueOrUpdatePendingPledgeCreateAsync(
                    workflowId: _workflow.WorkflowId,
                    constituentId: ConstituentId,
                    amount: amount,
                    pledgeDate: PledgeDate.Value,
                    fundId: FundIdText.Trim(),
                    comments: req.Comments,
                    requestJson: requestJson,
                    clientMachineName: _workflow.MachineName,
                    clientWindowsUser: _workflow.WindowsUser);

                // Reset API result fields: server worker will handle SKY submission.
                _workflow.Api.Attempted = false;
                _workflow.Api.AttemptedAtUtc = null;
                _workflow.Api.Success = false;
                _workflow.Api.GiftId = null;
                _workflow.Api.CreateResponseJson = null;
                _workflow.Api.InstallmentListJson = null;
                _workflow.Api.InstallmentAddJson = null;
                _workflow.Api.ErrorMessage = null;

                _workflow.Status = WorkflowStatus.ReadyToSubmit;
                _workflow.AddTrail("SkyTransactionQueued", $"CGSKYTransactions.SkyTransactionRecordId={queueId}");

                StatusText = $"Saved pledge (queued for server). Queue Id: {queueId}";
                RequestClose?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _workflow.Api.Success = false;
                _workflow.Api.ErrorMessage = ex.Message;

                _workflow.Status = WorkflowStatus.ApiFailed;

                StatusText = "Save error: " + ex.Message;

                TryAppendErrorLog(ex);
            }
            finally
            {
                _workflow.CompletedAtUtc = DateTime.UtcNow;

                // ALWAYS commit locally (success or failure) so nothing is lost.
                try
                {
                    await _workflowStore.SaveAsync(_workflow);
                    _workflow.Status = WorkflowStatus.Committed;
                }
                catch (Exception ex)
                {
                    _workflow.Status = WorkflowStatus.CommitFailed;
                    StatusText = StatusText + " (Local SQL save failed: " + ex.Message + ")";
                    TryAppendErrorLog(ex);
                }

                IsBusy = false;
            }
        }
        private static void TryAppendErrorLog(Exception ex)
        {
            try
            {
                _ = ErrorLogger.Log(ex, "GiftEntryViewModel");
            }
            catch
            {
                // ignore - logging should never crash the app
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}