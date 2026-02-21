using CheerfulGiverNXT.Services;
using CheerfulGiverNXT.ViewModels;
using CheerfulGiverNXT.Workflow;
using System;
using System.Configuration;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace CheerfulGiverNXT
{
    public partial class GiftWindow : Window
    {
        private readonly RenxtConstituentLookupService.ConstituentGridRow _row;
        private readonly GiftWorkflowContext _workflow;
        private bool _isNewRadioConstituent;

        public GiftWindow(RenxtConstituentLookupService.ConstituentGridRow row, GiftWorkflowContext workflow)
        {
            _row = row ?? throw new ArgumentNullException(nameof(row));
            _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));

            InitializeComponent();

            var vm = new GiftEntryViewModel(row, _workflow, App.GiftService, App.GiftWorkflowStore);
            vm.RequestClose += (_, __) => Close();
            DataContext = vm;

            Loaded += GiftWindow_Loaded;
        }

        private async void GiftWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= GiftWindow_Loaded;

            await Dispatcher.BeginInvoke(new Action(() =>
            {
                AmountTextBox?.Focus();
                AmountTextBox?.SelectAll();
            }), DispatcherPriority.Input);

            Mouse.OverrideCursor = null;
            Cursor = Cursors.Arrow;

            try
            {
                var tokens = LoadRadioFundTokens();
                if (tokens.Length == 0)
                {
                    _isNewRadioConstituent = false;
                    return;
                }

                var funds = await App.ConstituentService.GetContributedFundsAsync(_row.Id, maxGiftsToScan: 1000);
                var hasMatch = funds.Any(f => tokens.Any(t => TokenMatches(t, f.Name)));

                _isNewRadioConstituent = !hasMatch;
            }
            catch
            {
                _isNewRadioConstituent = false;
            }
            finally
            {
                // Persist into workflow so local SQL includes it.
                _workflow.IsNewRadioConstituent = _isNewRadioConstituent;

                ApplyBanner();
                Mouse.OverrideCursor = null;
                Cursor = Cursors.Arrow;
            }
        }

        private void ApplyBanner()
        {
            if (NewConstituentBanner is null) return;
            NewConstituentBanner.Visibility = _isNewRadioConstituent ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string[] LoadRadioFundTokens()
        {
            var raw = (ConfigurationManager.AppSettings["RadioFunds"] ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

            return raw
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => (t ?? "").Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool TokenMatches(string token, string? fundName)
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(fundName)) return false;

            token = token.Trim();
            fundName = fundName.Trim();

            // Prefer a "whole token" match to avoid NT matching in "PlaNT".
            var escaped = Regex.Escape(token);
            var pattern = $@"(?<![A-Za-z0-9]){escaped}(?![A-Za-z0-9])";

            return Regex.IsMatch(fundName, pattern, RegexOptions.IgnoreCase);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}