// GiftEntryViewModel.cs
// Pledge entry screen VM (pledge commitments only) with a friendly “One-time” option.
// Adds: a single workflow/transaction object as the source of truth.
// On Save: attempts SKY API call then ALWAYS commits workflow to SQL Express (success or failure).

using CheerfulGiverNXT.Data;
using CheerfulGiverNXT.Infrastructure;
using CheerfulGiverNXT.Services;
using CheerfulGiverNXT.Workflow;
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;

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
            new("Weekly",    PledgeFrequency.Weekly,    4,  false, false),
        };

        private static readonly SponsorshipOption[] HalfDaySponsorshipOptions =
        {
            new("Half-day AM", 1000m),
            new("Half-day PM", 1000m),
        };

        private static readonly SponsorshipOption[] FullDaySponsorshipOptions =
        {
            new("Full day", 2000m),
        };

        private readonly RenxtConstituentLookupService.ConstituentGridRow _row;
        private readonly RenxtGiftServer _gifts;

        // NEW: workflow source of truth + SQL store
        private readonly GiftWorkflowContext _workflow;
        private readonly IGiftWorkflowStore _workflowStore;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? RequestClose;

        public GiftWorkflowContext Workflow => _workflow;

        public int ConstituentId => _row.Id;
        public string FullName => _row.FullName;

        public string ConstituentSummary
        {
            get
            {
                var spouse = string.IsNullOrWhiteSpace(_row.Spouse) ? "" : _row.Spouse.Trim();

                var line1 = $"ID: {_row.Id}" + (string.IsNullOrWhiteSpace(spouse) ? "" : $" Spouse: {spouse}");

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
            set { _campaignIdText = value; OnPropertyChanged(); }
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

        public bool ShowSponsorshipSelector => EligibleSponsorshipOptions.Length > 0;

        private SponsorshipOption? _selectedSponsorshipOption;
        public SponsorshipOption? SelectedSponsorshipOption
        {
            get => _selectedSponsorshipOption;
            set { _selectedSponsorshipOption = value; OnPropertyChanged(); }
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
            IGiftWorkflowStore workflowStore)
        {
            _row = row ?? throw new ArgumentNullException(nameof(row));
            _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
            _gifts = giftServer ?? throw new ArgumentNullException(nameof(giftServer));
            _workflowStore = workflowStore ?? throw new ArgumentNullException(nameof(workflowStore));

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

            RefreshSponsorshipEligibility();
            RefreshCanSave();
        }

        private void RefreshSponsorshipEligibility()
        {
            if (!TryParseAmount(out var amt) || amt <= 0m)
            {
                EligibleSponsorshipOptions = Array.Empty<SponsorshipOption>();
                SelectedSponsorshipOption = null;
                SponsorshipDate = null;
                return;
            }

            SponsorshipOption[] eligible;
            if (amt >= 2000m)
            {
                eligible = FullDaySponsorshipOptions;
            }
            else if (amt >= 1000m)
            {
                eligible = HalfDaySponsorshipOptions;
            }
            else
            {
                eligible = Array.Empty<SponsorshipOption>();
            }

            EligibleSponsorshipOptions = eligible;

            if (eligible.Length == 0)
            {
                SelectedSponsorshipOption = null;
                SponsorshipDate = null;
                return;
            }

            // Keep selection if still eligible, else default to first eligible.
            if (_selectedSponsorshipOption is null || !eligible.Any(x => x.Display == _selectedSponsorshipOption.Display))
                SelectedSponsorshipOption = eligible[0];

            // If sponsorship becomes eligible and no date has been chosen yet, default to today.
            if (SponsorshipDate is null)
                SponsorshipDate = DateTime.Today;
        }

        private void RefreshCanSave()
        {
            CanSave = !IsBusy
                      && TryParseAmount(out var amt) && amt > 0m
                      && PledgeDate.HasValue
                      && FirstInstallmentDate.HasValue
                      && TryParseInstallments(out var n) && n > 0
                      && !string.IsNullOrWhiteSpace(FundIdText);
        }

        private static bool TryParseInt(string? s, out int value) =>
            int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        private bool TryParseInstallments(out int n) => TryParseInt(NumberOfInstallmentsText, out n);

        private bool TryParseAmount(out decimal amount) =>
            decimal.TryParse(AmountText, NumberStyles.Number, CultureInfo.CurrentCulture, out amount);

        private async Task SaveAsync()
        {
            if (!TryParseAmount(out var amount) || amount <= 0m) { StatusText = "Enter a valid amount."; return; }
            if (!PledgeDate.HasValue) { StatusText = "Select a pledge date."; return; }
            if (!FirstInstallmentDate.HasValue) { StatusText = "Select a first installment date."; return; }
            if (!TryParseInstallments(out var installments) || installments <= 0) { StatusText = "# of installments must be a whole number."; return; }
            if (string.IsNullOrWhiteSpace(FundIdText)) { StatusText = "Fund ID is required."; return; }

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
            StatusText = "Creating pledge...";

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
                    CampaignId: string.IsNullOrWhiteSpace(CampaignIdText) ? null : CampaignIdText.Trim(),
                    AppealId: string.IsNullOrWhiteSpace(AppealIdText) ? null : AppealIdText.Trim(),
                    PackageId: string.IsNullOrWhiteSpace(PackageIdText) ? null : PackageIdText.Trim(),

                    VerifyInstallmentsAfterCreate: true,
                    AddInstallmentsIfMissing: true
                );

                _workflow.Api.Attempted = true;
                _workflow.Api.AttemptedAtUtc = DateTime.UtcNow;
                _workflow.Api.RequestJson = JsonSerializer.Serialize(req);

                var result = await _gifts.CreatePledgeAsync(req);

                _workflow.Api.Success = true;
                _workflow.Api.GiftId = result.GiftId;
                _workflow.Api.CreateResponseJson = result.RawCreateResponseJson;
                _workflow.Api.InstallmentListJson = result.RawInstallmentListJson;
                _workflow.Api.InstallmentAddJson = result.RawInstallmentAddJson;

                _workflow.Status = WorkflowStatus.ApiSucceeded;

                StatusText = $"Saved pledge! Gift ID: {result.GiftId}";
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
            // Keep your existing log file path, but also fall back to LocalAppData if needed.
            try
            {
                File.AppendAllText(
                    "D:\\CodeProjects\\CheerfulGiverNXT\\CheerfulErrors.txt",
                    $"{DateTime.Now}: An error occurred: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}"
                );
                return;
            }
            catch { /* ignore */ }

            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CheerfulGiverNXT");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "CheerfulErrors.txt");
                File.AppendAllText(path, $"{DateTime.Now}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");
            }
            catch { /* ignore */ }
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
