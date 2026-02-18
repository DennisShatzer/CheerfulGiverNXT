using System;
using System.Collections.Generic;
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
        private bool _newConstituent;

        public GiftWindow(RenxtConstituentLookupService.ConstituentGridRow row)
        {
            _row = row ?? throw new ArgumentNullException(nameof(row));

            InitializeComponent();

            var vm = new GiftEntryViewModel(_row, App.GiftService);
            vm.RequestClose += (_, __) => Close();
            DataContext = vm;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private async void GiftWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Run once.
            Loaded -= GiftWindow_Loaded;

            // Make sure the operator can start typing immediately.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AmountTextBox?.Focus();
                AmountTextBox?.SelectAll();
            }), DispatcherPriority.Input);

            // Clear any lingering busy cursor (some callers set Mouse.OverrideCursor).
            Mouse.OverrideCursor = null;
            Cursor = Cursors.Arrow;

            try
            {
                var tokens = LoadRadioFundTokens();
                if (tokens.Count == 0)
                {
                    // If no config tokens exist, do not show the "new" banner.
                    _newConstituent = false;
                    return;
                }

                // Pull giving history funds via Gift API (gift_splits -> fund_id) then resolve names.
                var funds = await App.ConstituentService.GetContributedFundsAsync(_row.Id, maxGiftsToScan: 1000);

                var hasMatch = funds.Any(f => tokens.Any(t => TokenMatches(t, f.Name)));

                // If a match is found => NOT new. If not found => new.
                _newConstituent = !hasMatch;
            }
            catch
            {
                // If we can't determine, keep the banner hidden (avoid false positives).
                _newConstituent = false;
            }
            finally
            {
                ApplyBanner();
                Mouse.OverrideCursor = null;
                Cursor = Cursors.Arrow;
            }
        }

        private void ApplyBanner()
        {
            if (NewConstituentBanner is null)
                return;

            NewConstituentBanner.Visibility = _newConstituent ? Visibility.Visible : Visibility.Collapsed;
        }

        private static List<string> LoadRadioFundTokens()
        {
            var raw = (ConfigurationManager.AppSettings["RadioFunds"] ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            return raw
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool TokenMatches(string token, string fundName)
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(fundName))
                return false;

            // Whole-word, case-insensitive match (reduces false positives for short tokens like "NT").
            return Regex.IsMatch(
                fundName,
                $@"\b{Regex.Escape(token)}\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
