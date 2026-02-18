using System.Windows;

namespace CheerfulGiverNXT
{
    public partial class GiftWindow : Window
    {
        public GiftWindow(RenxtConstituentLookupService.ConstituentGridRow row, string accessToken, string subscriptionKey)
        {
            InitializeComponent();

            var vm = new GiftEntryViewModel(row, accessToken, subscriptionKey);
            vm.RequestClose += (_, __) => Close();
            DataContext = vm;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
