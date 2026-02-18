using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CheerfulGiverNXT
{
    public partial class GiftWindow : Window
    {
        private readonly RenxtConstituentLookupService.ConstituentGridRow _row;

        public GiftWindow(RenxtConstituentLookupService.ConstituentGridRow row)
        {
            InitializeComponent();

            _row = row;

            var vm = new GiftEntryViewModel(row, App.GiftService);
            vm.RequestClose += (_, __) => Close();
            DataContext = vm;

            Loaded += async (_, __) => await LoadPriorFundsAsync();
        }

        private async Task LoadPriorFundsAsync()
        {
            try
            {
                // If the controls aren't present (designer issues), just bail safely.
                if (PriorFundsComboBox is null || PriorFundsLabel is null)
                    return;

                // Show "loading" UX if the VM has StatusText.
                if (DataContext is GiftEntryViewModel vm)
                    vm.StatusText = "Loading previous funds…";

                var funds = await App.ConstituentService.GetContributedFundsAsync(_row.Id, maxGiftsToScan: 500);

                if (funds is null || funds.Count == 0)
                {
                    PriorFundsLabel.Visibility = Visibility.Collapsed;
                    PriorFundsComboBox.Visibility = Visibility.Collapsed;

                    if (DataContext is GiftEntryViewModel vm2)
                        vm2.StatusText = "Ready.";

                    return;
                }

                PriorFundsComboBox.ItemsSource = funds;

                // If FundIdText is empty and there's only one prior fund, prefill it.
                if (DataContext is GiftEntryViewModel vm3 &&
                    string.IsNullOrWhiteSpace(vm3.FundIdText) &&
                    funds.Count == 1)
                {
                    vm3.FundIdText = funds[0].Id.ToString();
                    PriorFundsComboBox.SelectedValue = funds[0].Id;
                }

                if (DataContext is GiftEntryViewModel vm4)
                    vm4.StatusText = "Ready.";
            }
            catch
            {
                // Don’t block gift entry if fund lookup fails.
                if (PriorFundsLabel is not null) PriorFundsLabel.Visibility = Visibility.Collapsed;
                if (PriorFundsComboBox is not null) PriorFundsComboBox.Visibility = Visibility.Collapsed;

                if (DataContext is GiftEntryViewModel vm)
                    vm.StatusText = "Ready. (Could not load previous funds.)";
            }
        }

        private void PriorFundsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is not GiftEntryViewModel vm) return;
            if (sender is not ComboBox cb) return;

            if (cb.SelectedValue is int fundId && fundId > 0)
            {
                vm.FundIdText = fundId.ToString();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
