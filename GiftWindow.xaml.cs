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

        private bool _isNewRadioConstituent;

        public GiftWindow(RenxtConstituentLookupService.ConstituentGridRow row)
        {
            _row = row;

            InitializeComponent();

            var vm = new GiftEntryViewModel(row, App.GiftService);
            vm.RequestClose += (_, __) => Close();
            DataContext = vm;

            Loaded += GiftWindow_Loaded;
        }

        private async void GiftWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= GiftWindow_Loaded;

            // Make sure the operator can start typing immediately (after layout).
            await Dispatcher.BeginInvoke(new Action(() =>
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
                if (tokens.Length == 0)
                {
                    // No configured tokens => do not show "new" banner.
                    _isNewRadioConstituent = false;
                    return;
                }

                // Pull giving-history funds via Gift API (gift_splits -> fund_id) then resolve names.
                var funds = await App.ConstituentService.GetContributedFundsAsync(_row.Id, maxGiftsToScan: 1000);

                var hasMatch = funds.Any(f => tokens.Any(t => TokenMatches(t, f.Name)));

                // If a match is found => NOT new. If not found => new.
                _isNewRadioConstituent = !hasMatch;
            }
            catch
            {
                // If we can't determine, keep the banner hidden (avoid false positives).
                _isNewRadioConstituent = false;
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
            if (NewConstituentBanner is null) return;

            NewConstituentBanner.Visibility = _isNewRadioConstituent
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private static string[] LoadRadioFundTokens()
        {
            var raw = (ConfigurationManager.AppSettings["RadioFunds"] ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return Array.Empty<string>();

            return raw
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => (t ?? "").Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool TokenMatches(string token, string? fundName)
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(fundName))
                return false;

            token = token.Trim();
            fundName = fundName.Trim();

            // Prefer a "whole token" match to avoid NT matching in "PlaNT".
            // Use boundaries based on alphanumerics.
            var escaped = Regex.Escape(token);
            var pattern = $@"(?<![A-Za-z0-9]){escaped}(?![A-Za-z0-9])";

            return Regex.IsMatch(fundName, pattern, RegexOptions.IgnoreCase);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
