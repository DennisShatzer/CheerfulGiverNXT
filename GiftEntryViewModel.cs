// GiftEntryViewModel.cs
// Pledge entry screen VM (pledge commitments only) with a friendly “One-time” option.
// Hidden fields (fund/campaign/appeal/package/installments/start-date/reminder) remain in the VM for processing.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace CheerfulGiverNXT
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

        private readonly RenxtConstituentLookupService.ConstituentGridRow _row;
        private readonly RenxtGiftServer _gifts;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? RequestClose;

        public int ConstituentId => _row.Id;
        public string FullName => _row.FullName;

        public string ConstituentSummary
        {
            get
            {
                var spouse = string.IsNullOrWhiteSpace(_row.Spouse) ? "" : _row.Spouse.Trim();
                var line1 = $"ID: {_row.Id}" + (string.IsNullOrWhiteSpace(spouse) ? "" : $"   Spouse: {spouse}");

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

                // Keep blank lines out if something is missing.
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
            private set { _frequency = value; OnPropertyChanged(); }
        }

        private string _numberOfInstallmentsText = "1";
        public string NumberOfInstallmentsText
        {
            get => _numberOfInstallmentsText;
            set { _numberOfInstallmentsText = value; OnPropertyChanged(); RefreshCanSave(); }
        }

        private bool _isInstallmentsLocked;
        public bool IsInstallmentsLocked
        {
            get => _isInstallmentsLocked;
            private set { _isInstallmentsLocked = value; OnPropertyChanged(); }
        }

        private bool _isStartDateLockedToPledgeDate;
        public bool IsStartDateLockedToPledgeDate
        {
            get => _isStartDateLockedToPledgeDate;
            private set { _isStartDateLockedToPledgeDate = value; OnPropertyChanged(); }
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
            set { _fundIdText = value; OnPropertyChanged(); RefreshCanSave(); }
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

        // Sponsorship eligibility (driven by AmountText + App.config)
        private readonly SponsorshipOption[] _allSponsorshipOptions;
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

        public GiftEntryViewModel(RenxtConstituentLookupService.ConstituentGridRow row, RenxtGiftServer giftServer)
        {
            _row = row ?? throw new ArgumentNullException(nameof(row));
            _gifts = giftServer ?? throw new ArgumentNullException(nameof(giftServer));

            _allSponsorshipOptions = LoadSponsorshipOptions();

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
            if (_allSponsorshipOptions.Length == 0)
            {
                EligibleSponsorshipOptions = Array.Empty<SponsorshipOption>();
                SelectedSponsorshipOption = null;
                SponsorshipDate = null;
                return;
            }

            if (!TryParseAmount(out var amt) || amt <= 0m)
            {
                EligibleSponsorshipOptions = Array.Empty<SponsorshipOption>();
                SelectedSponsorshipOption = null;
                SponsorshipDate = null;
                return;
            }

            // Qualify when amount meets or exceeds the configured threshold.
            var eligible = _allSponsorshipOptions
                .Where(o => amt >= o.ThresholdAmount)
                .OrderBy(o => o.ThresholdAmount)
                .ThenBy(o => o.Display, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            EligibleSponsorshipOptions = eligible;

            if (eligible.Length == 0)
            {
                SelectedSponsorshipOption = null;
                SponsorshipDate = null;
                return;
            }

            // Keep selection if still eligible, else default to first eligible.
            if (_selectedSponsorshipOption is null || !eligible.Any(x => x.Display == _selectedSponsorshipOption.Display && x.ThresholdAmount == _selectedSponsorshipOption.ThresholdAmount))
                SelectedSponsorshipOption = eligible[0];

            // If sponsorship becomes eligible and no date has been chosen yet, default to today.
            if (SponsorshipDate is null)
                SponsorshipDate = DateTime.Today;
        }

        private static SponsorshipOption[] LoadSponsorshipOptions()
        {
            // Preferred:
            // <add key="SponsorshipOptions" value="Half-day AM=1000;Half-day PM=1000;Full day=2000" />
            var raw = (ConfigurationManager.AppSettings["SponsorshipOptions"] ?? "").Trim();
            var list = new List<SponsorshipOption>();

            if (!string.IsNullOrWhiteSpace(raw))
            {
                foreach (var part in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split(new[] { '=' }, 2);
                    if (kv.Length != 2) continue;

                    var name = (kv[0] ?? "").Trim();
                    var amountText = (kv[1] ?? "").Trim();

                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var threshold)) continue;
                    if (threshold <= 0m) continue;

                    list.Add(new SponsorshipOption(name, threshold));
                }
            }

            // Alternate format:
            // <add key="Sponsorship:Half-day AM" value="1000" />
            // <add key="Sponsorship:Full day" value="2000" />
            if (list.Count == 0)
            {
                foreach (var key in ConfigurationManager.AppSettings.AllKeys ?? Array.Empty<string>())
                {
                    if (key is null) continue;
                    if (!key.StartsWith("Sponsorship:", StringComparison.OrdinalIgnoreCase)) continue;

                    var name = key.Substring("Sponsorship:".Length).Trim();
                    var amountText = (ConfigurationManager.AppSettings[key] ?? "").Trim();

                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var threshold)) continue;
                    if (threshold <= 0m) continue;

                    list.Add(new SponsorshipOption(name, threshold));
                }
            }

            // De-dup by name+threshold
            return list
                .GroupBy(o => (Name: o.Display.Trim(), o.ThresholdAmount))
                .Select(g => g.First())
                .ToArray();
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

                var result = await _gifts.CreatePledgeAsync(req);

                StatusText = $"Saved pledge! Gift ID: {result.GiftId}";
                RequestClose?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                StatusText = "Save error: " + ex.Message;

                // Keep your existing log file path as-is
                try
                {
                    File.AppendAllText(
                        "D:\\CodeProjects\\CheerfulGiverNXT\\CheerfulErrors.txt",
                        $"{DateTime.Now}: An error occurred: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}"
                    );
                }
                catch { /* ignore */ }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
