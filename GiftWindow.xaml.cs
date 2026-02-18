using System;
using System.Configuration;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace CheerfulGiverNXT
{
    public partial class GiftWindow : Window
    {
        private readonly RenxtConstituentLookupService.ConstituentGridRow _row;

        /// <summary>
        /// True when no matching "RadioFunds" tokens were found in this constituent's giving history.
        /// This flag is computed in code-behind when the window loads.
        /// </summary>
        public bool NewConstituent { get; private set; }

        public GiftWindow(RenxtConstituentLookupService.ConstituentGridRow row)
        {
            InitializeComponent();

            _row = row;

            // Defensive: if anything left the app in a forced "busy" cursor state,
            // clear it when this modal entry window opens.
            Mouse.OverrideCursor = null;
            Cursor = Cursors.Arrow;
            Closed += (_, __) => Mouse.OverrideCursor = null;

            var vm = new GiftEntryViewModel(row, App.GiftService);
            vm.RequestClose += (_, __) => Close();
            DataContext = vm;

            Loaded += GiftWindow_Loaded;
        }

        private async void GiftWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= GiftWindow_Loaded;

            // Make sure the operator can start typing immediately.
            AmountTextBox?.Focus();
            AmountTextBox?.SelectAll();

            // Also clear any lingering busy cursor (some callers set Mouse.OverrideCursor).
            Mouse.OverrideCursor = null;
            Cursor = Cursors.Arrow;

            try
            {
                var tokens = LoadRadioFundTokens();
                if (tokens.Count == 0)
                {
                    // If no config tokens exist, do not show the "new" banner.
                    NewConstituent = false;
                    return;
                }

                // Pull giving history funds via Gift API (gift_splits -> fund_id) then resolve names.
                var funds = await App.ConstituentService.GetContributedFundsAsync(_row.Id, maxGiftsToScan: 1000);

                var hasMatch = funds.Any(f => tokens.Any(t => TokenMatches(t, f.Name)));

                // If a match is found => NOT new. If not found => new.
                NewConstituent = !hasMatch;
            }
            catch
            {
                // If we can't determine, keep the banner hidden (avoid false positives).
                NewConstituent = false;
            }
            finally
            {
                ApplyBanner();
                Mouse.OverrideCursor = null;
            }
        }

        private void ApplyBanner()
        {
            if (NewConstituentBanner is not null)
                NewConstituentBanner.Visibility = NewConstituent ? Visibility.Visible : Visibility.Collapsed;
        }

        private static HashSet<string> LoadRadioFundTokens()
        {
            // App.config:
            // <add key="RadioFunds" value="RADIO;NT;RBF;WCRH;" />
            var raw = ConfigurationManager.AppSettings["RadioFunds"] ?? "";

            return raw
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Select(x => x.ToUpperInvariant())
                .ToHashSet();
        }

        private static bool TokenMatches(string token, string fundName)
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(fundName))
                return false;

            // Match whole token in the fund description/name (case-insensitive).
            return Regex.IsMatch(
                fundName,
                $@"\b{Regex.Escape(token)}\b",
                RegexOptions.IgnoreCase);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
