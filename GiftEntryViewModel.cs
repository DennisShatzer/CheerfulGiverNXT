// GiftEntryViewModel.cs
// Pledge entry screen VM (pledge commitments only) with a friendly “One-time (single installment)” option.
// Sponsorship selector options (shown when Amount qualifies) are read from App.config.
//
// App.config examples:
//
// Option A (recommended single key):
//   <add key="SponsorshipOptions" value="Half-day AM=1000;Half-day PM=1000;Full day=2000" />
//
// Option B (individual keys with a prefix):
//   <add key="Sponsorship:Half-day AM" value="1000" />
//   <add key="Sponsorship:Half-day PM" value="1000" />
//   <add key="Sponsorship:Full day" value="2000" />
//
// Option C (keys are the display names; values are amounts):
//   <add key="Half-day AM" value="1000" />
//   <add key="Half-day PM" value="1000" />
//   <add key="Full day" value="2000" />
//
// Requires RenxtGiftServer.cs and AsyncRelayCommand.cs in the same project/namespace.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        public sealed record SponsorshipOption(string Display, decimal RequiredAmount)
        {
            public override string ToString() => Display;
        }

        private static readonly SponsorshipOption[] ConfiguredSponsorshipOptions = LoadSponsorshipOptionsFromConfig();

        public FrequencyPreset[] FrequencyPresets { get; } =
        {
            // “One-time donation” UX: still uses Monthly in API (frequency value is required),
            // but forces number_of_installments = 1 and start_date = pledge_date.
            new("One-time (single installment)", PledgeFrequency.Monthly, 1,  true,  true),
            new("Monthly",                       PledgeFrequency.Monthly, 12, false, false),
            new("Quarterly",                     PledgeFrequency.Quarterly, 4,  false, false),
            new("Annually",                      PledgeFrequency.Annually, 1,  false, false),
            new("Weekly",                        PledgeFrequency.Weekly,  4,  false, false),
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
                var line1 = $"ID: {_row.Id}   Spouse: {_row.Spouse}".TrimEnd();

                var street = (_row.Street ?? "").Trim();
                var city = (_row.City ?? "").Trim();
                var state = (_row.State ?? "").Trim();
                var zip = (_row.Zip ?? "").Trim();

                var line2 = street;

                // Prefer "City, ST ZIP" formatting.
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(city)) parts.Add(city.TrimEnd(','));
                if (!string.IsNullOrWhiteSpace(state)) parts.Add(state);
                if (!string.IsNullOrWhiteSpace(zip)) parts.Add(zip);

                var line3 = "";
                if (parts.Count > 0)
                {
                    if (parts.Count >= 2)
                        line3 = $"{parts[0]}, {string.Join(" ", parts.Skip(1))}";
                    else
                        line3 = parts[0];
                }

                if (string.IsNullOrWhiteSpace(line2) && string.IsNullOrWhiteSpace(line3))
                    return line1;

                if (string.IsNullOrWhiteSpace(line2))
                    return $"{line1}\n{line3}";

                if (string.IsNullOrWhiteSpace(line3))
                    return $"{line1}\n{line2}";

                return $"{line1}\n{line2}\n{line3}";
            }
        }

        private string _amountText = "";
        public string AmountText
        {
            get => _amountText;
            set
            {
                _amountText = value;
                OnPropertyChanged();
                UpdateSponsorshipEligibility();
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

        private DateTime? _firstInstallmentDate = DateTime.Today.AddMonths(1);
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

        private string _numberOfInstallmentsText = "12";
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

        // These are kept "behind the scenes" for processing even if the UI hides them.
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

        // Sponsorship selector (shown only when Amount qualifies)
        public ObservableCollection<SponsorshipOption> EligibleSponsorshipOptions { get; } = new();

        private SponsorshipOption? _selectedSponsorshipOption;
        public SponsorshipOption? SelectedSponsorshipOption
        {
            get => _selectedSponsorshipOption;
            set { _selectedSponsorshipOption = value; OnPropertyChanged(); }
        }

        public bool ShowSponsorshipSelector => EligibleSponsorshipOptions.Count > 0;

        private string _comments = "";
        public string Comments
        {
            get => _comments;
            set { _comments = value; OnPropertyChanged(); }
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
            private set { _canSave = value; OnPropertyChanged(); SaveCommand.RaiseCanExecuteChanged(); }
        }

        public AsyncRelayCommand SaveCommand { get; }

        public GiftEntryViewModel(RenxtConstituentLookupService.ConstituentGridRow row, RenxtGiftServer giftServer)
        {
            _row = row ?? throw new ArgumentNullException(nameof(row));
            _gifts = giftServer ?? throw new ArgumentNullException(nameof(giftServer));

            // Default to One-time as requested.
            _selectedPreset = FrequencyPresets[0];
            Frequency = _selectedPreset.ApiFrequency;
            IsInstallmentsLocked = _selectedPreset.LockInstallments;
            IsStartDateLockedToPledgeDate = _selectedPreset.LockStartDateToPledgeDate;

            SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave);

            NumberOfInstallmentsText = _selectedPreset.DefaultInstallments.ToString(CultureInfo.InvariantCulture);

            // If One-time locks start date to pledge date, keep them in sync on startup.
            if (IsStartDateLockedToPledgeDate && PledgeDate.HasValue)
                FirstInstallmentDate = PledgeDate.Value.Date;

            UpdateSponsorshipEligibility();
            RefreshCanSave();
        }

        private void UpdateSponsorshipEligibility()
        {
            EligibleSponsorshipOptions.Clear();

            if (ConfiguredSponsorshipOptions.Length == 0)
            {
                if (SelectedSponsorshipOption is not null)
                    SelectedSponsorshipOption = null;

                OnPropertyChanged(nameof(ShowSponsorshipSelector));
                return;
            }

            if (!TryParseAmount(out var amount) || amount <= 0m)
            {
                if (SelectedSponsorshipOption is not null)
                    SelectedSponsorshipOption = null;

                OnPropertyChanged(nameof(ShowSponsorshipSelector));
                return;
            }

            var eligible = ConfiguredSponsorshipOptions
                .Where(o => amount >= o.RequiredAmount)
                .OrderBy(o => o.RequiredAmount)
                .ThenBy(o => o.Display, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var opt in eligible)
                EligibleSponsorshipOptions.Add(opt);

            if (EligibleSponsorshipOptions.Count == 0)
            {
                SelectedSponsorshipOption = null;
            }
            else
            {
                if (SelectedSponsorshipOption is null || !EligibleSponsorshipOptions.Contains(SelectedSponsorshipOption))
                    SelectedSponsorshipOption = EligibleSponsorshipOptions[0];
            }

            OnPropertyChanged(nameof(ShowSponsorshipSelector));
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

        private static SponsorshipOption[] LoadSponsorshipOptionsFromConfig()
        {
            try
            {
                var list = new List<SponsorshipOption>();

                void Add(string display, decimal amt)
                {
                    if (string.IsNullOrWhiteSpace(display)) return;
                    if (amt <= 0m) return;

                    list.Add(new SponsorshipOption(display.Trim(), amt));
                }

                // 1) Packed list: SponsorshipOptions="Half-day AM=1000;Half-day PM=1000;Full day=2000"
                var packed = (ConfigurationManager.AppSettings["SponsorshipOptions"] ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(packed))
                {
                    foreach (var part in packed.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = part.Split(new[] { '=' }, 2);
                        if (kv.Length != 2) continue;

                        var name = kv[0].Trim();
                        var val = kv[1].Trim();

                        if (decimal.TryParse(val, NumberStyles.Number, CultureInfo.InvariantCulture, out var amt) ||
                            decimal.TryParse(val, NumberStyles.Number, CultureInfo.CurrentCulture, out amt))
                        {
                            Add(name, amt);
                        }
                    }

                    return list.DistinctBy(o => (o.Display, o.RequiredAmount)).ToArray();
                }

                // 2) Prefixed keys: Sponsorship:Half-day AM=1000
                foreach (var key in ConfigurationManager.AppSettings.AllKeys)
                {
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    var val = ConfigurationManager.AppSettings[key];
                    if (string.IsNullOrWhiteSpace(val)) continue;

                    string? display = null;

                    if (key.StartsWith("Sponsorship:", StringComparison.OrdinalIgnoreCase))
                        display = key.Substring("Sponsorship:".Length);
                    else if (key.StartsWith("Sponsorship.", StringComparison.OrdinalIgnoreCase))
                        display = key.Substring("Sponsorship.".Length);
                    else if (key.StartsWith("Sponsorship_", StringComparison.OrdinalIgnoreCase))
                        display = key.Substring("Sponsorship_".Length);

                    if (display is null) continue;

                    if (decimal.TryParse(val.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amt) ||
                        decimal.TryParse(val.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out amt))
                    {
                        Add(display, amt);
                    }
                }

                if (list.Count > 0)
                    return list.DistinctBy(o => (o.Display, o.RequiredAmount)).ToArray();

                // 3) Fallback: treat keys as display names if value parses as a number.
                foreach (var key in ConfigurationManager.AppSettings.AllKeys)
                {
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    var val = ConfigurationManager.AppSettings[key];
                    if (string.IsNullOrWhiteSpace(val)) continue;

                    // Don't accidentally treat known non-option keys as options.
                    if (key.Equals("RadioFunds", StringComparison.OrdinalIgnoreCase)) continue;
                    if (key.Equals("SubscriptionKey", StringComparison.OrdinalIgnoreCase)) continue;

                    if (decimal.TryParse(val.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amt) ||
                        decimal.TryParse(val.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out amt))
                    {
                        Add(key, amt);
                    }
                }

                return list.DistinctBy(o => (o.Display, o.RequiredAmount)).ToArray();
            }
            catch
            {
                return Array.Empty<SponsorshipOption>();
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    internal static class LinqCompatExtensions
    {
        // .NET 6+ has DistinctBy in LINQ; if you're on older, this keeps things compiling.
        public static IEnumerable<TSource> DistinctBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)
        {
            var seen = new HashSet<TKey>();
            foreach (var item in source)
            {
                if (seen.Add(keySelector(item)))
                    yield return item;
            }
        }
    }
}
