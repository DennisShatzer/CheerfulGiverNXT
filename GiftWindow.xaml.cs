using System.Windows;

namespace CheerfulGiverNXT
{
    public partial class GiftWindow : Window
    {
        public GiftWindow(RenxtConstituentLookupService.ConstituentGridRow row)
        {
            InitializeComponent();

            var vm = new GiftEntryViewModel(row, App.GiftService);
            vm.RequestClose += (_, __) => Close();
            DataContext = vm;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
