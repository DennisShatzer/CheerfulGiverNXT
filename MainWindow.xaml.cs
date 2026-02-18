using System.Windows;
using System.Windows.Input;

namespace CheerfulGiverNXT
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new ConstituentLookupTestViewModel();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            if (DataContext is ConstituentLookupTestViewModel vm &&
                vm.SearchCommand.CanExecute(null))
            {
                vm.SearchCommand.Execute(null);
                e.Handled = true;
            }
        }

        private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not ConstituentLookupTestViewModel vm) return;
            if (vm.SelectedRow is null) return;

            // Open Gift entry window
            var w = new GiftWindow(vm.SelectedRow, vm.AccessToken, vm.SubscriptionKey);
            w.Owner = this;
            w.ShowDialog();
        }
    }
}
