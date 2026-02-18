// GiftEntryViewModel.cs
// Pledge entry screen VM (pledge commitments only) with a friendly “One-time” option.
// Requires RenxtGiftServer.cs and AsyncRelayCommand.cs in the same project/namespace.

using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
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

        public FrequencyPreset[] FrequencyPresets { get; } =
        {
            // “One-time donation” UX: still uses Monthly in API (frequency value is required),
            // but forces number_of_installments = 1 and start_date = pledge_date.
            new("One-time",                     PledgeFrequency.Monthly, 1,  true,  true),
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

        public string ConstituentSummary =>
            $"ID: {_row.Id}   Spouse: {_row.Spouse}\n{_row.Street}, {_row.City}, {_row.State} {_row.Zip}";

        private string _amountText = "";
        public string AmountText
        {
            get => _amountText;
            set { _amountText = value; OnPropertyChanged(); RefreshCanSave(); }
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

                if (IsStartDateLockedToPledgeDate)
                {
                    // One-time: force start date to pledge date.
                    if (PledgeDate.HasValue)
                        FirstInstallmentDate = PledgeDate.Value.Date;
                }
                else
                {
                    // If the operator switches away from One-time, default the first installment to next month
                    // (keeps the old behavior without exposing the date field in the UI).
                    if (PledgeDate.HasValue)
                    {
                        var pd = PledgeDate.Value.Date;
                        if (!FirstInstallmentDate.HasValue || FirstInstallmentDate.Value.Date == pd)
                            FirstInstallmentDate = pd.AddMonths(1);
                    }
                    else if (!FirstInstallmentDate.HasValue)
                    {
                        FirstInstallmentDate = DateTime.Today.AddMonths(1);
                    }
                }

                RefreshCanSave();
}
        }

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

            
            SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave);

            // Default the dropdown to "One-time"
            SelectedPreset = FrequencyPresets[0];
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
