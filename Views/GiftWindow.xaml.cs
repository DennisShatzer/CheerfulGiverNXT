using CheerfulGiverNXT.Services;
using CheerfulGiverNXT.ViewModels;
using CheerfulGiverNXT.Workflow;
using System;
using System.Configuration;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
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

        private DispatcherTimer? _blackoutTimer;

        public GiftWindow(RenxtConstituentLookupService.ConstituentGridRow row, GiftWorkflowContext workflow)
        {
            _row = row ?? throw new ArgumentNullException(nameof(row));
            _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));

            InitializeComponent();

            var vm = new GiftEntryViewModel(row, _workflow, App.GiftService, App.GiftWorkflowStore);
            vm.RequestClose += (_, __) => Close();
            DataContext = vm;

            HookSponsorshipBlackoutDates(vm);

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



        private void HookSponsorshipBlackoutDates(GiftEntryViewModel vm)
        {
            if (SponsorshipDatePicker is null) return;

            _blackoutTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };

            _blackoutTimer.Tick += (_, __) =>
            {
                _blackoutTimer?.Stop();
                ApplySponsorshipBlackoutDates(vm);
            };

            void RequestApply()
            {
                if (_blackoutTimer is null) return;
                _blackoutTimer.Stop();
                _blackoutTimer.Start();
            }

            vm.FullyBookedSponsorshipDates.CollectionChanged += (_, __) => RequestApply();
            vm.PropertyChanged += (_, e) =>
            {
                // When sponsorship selector becomes visible, ensure the picker updates.
                if (e.PropertyName == nameof(GiftEntryViewModel.ShowSponsorshipSelector))
                    RequestApply();
            };

            // Initial apply
            RequestApply();
        }

        private void ApplySponsorshipBlackoutDates(GiftEntryViewModel vm)
        {
            if (SponsorshipDatePicker is null) return;

            var dates = vm.FullyBookedSponsorshipDates
                .Select(d => d.Date)
                .Distinct()
                .OrderBy(d => d)
                .ToArray();

            SponsorshipDatePicker.BlackoutDates.Clear();

            foreach (var range in BuildDateRanges(dates))
                SponsorshipDatePicker.BlackoutDates.Add(range);
        }

        private static IEnumerable<CalendarDateRange> BuildDateRanges(DateTime[] dates)
        {
            if (dates.Length == 0)
                yield break;

            var start = dates[0];
            var end = dates[0];

            for (int i = 1; i < dates.Length; i++)
            {
                var d = dates[i];
                if (d == end.AddDays(1))
                {
                    end = d;
                    continue;
                }

                yield return new CalendarDateRange(start, end);
                start = end = d;
            }

            yield return new CalendarDateRange(start, end);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}