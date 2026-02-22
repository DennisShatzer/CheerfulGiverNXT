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
        private bool _isFirstTimeGiver;

        private DispatcherTimer? _blackoutTimer;

        public GiftWindow(RenxtConstituentLookupService.ConstituentGridRow row, GiftWorkflowContext workflow)
        {
            _row = row ?? throw new ArgumentNullException(nameof(row));
            _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));

            InitializeComponent();

            var vm = new GiftEntryViewModel(row, _workflow, App.GiftService, App.GiftWorkflowStore, App.CampaignContext, App.GiftMatchService);
            vm.RequestClose += (_, __) => Close();
            DataContext = vm;

            HookSponsorshipBlackoutDates(vm);
            HookGiftHistoryAutoCollapseOnLoad(vm);

            PreviewKeyDown += GiftWindow_PreviewKeyDown;
            Loaded += GiftWindow_Loaded;
        }

        private void GiftWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            Window? dialog = null;

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
                && (e.Key == Key.F12 || e.SystemKey == Key.F12))
            {
                dialog = new CampaignsAdminWindow();
            }
            else if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift)
                     && (e.Key == Key.F || e.SystemKey == Key.F))
            {
                dialog = new FirstTimeFundExclusionsWindow();
            }

            if (dialog is null) return;

            e.Handled = true;
            dialog.Owner = this;
            dialog.ShowDialog();
        }

        private void HookGiftHistoryAutoCollapseOnLoad(GiftEntryViewModel vm)
        {
            // Cosmetic: If no gifts exist for the current campaign, start with the history expander collapsed
            // so the operator doesn't see an empty grid.
            if (GiftHistoryExpander is null) return;

            bool applied = false;

            void Unhook()
            {
                vm.PropertyChanged -= Vm_PropertyChanged;
                vm.ExistingCampaignGifts.CollectionChanged -= ExistingCampaignGifts_CollectionChanged;
            }

            void TryApply()
            {
                if (applied) return;
                if (vm.IsLoadingExistingCampaignGifts) return;

                // Only collapse for the normal "no gifts" case (not when an error message is present).
                var hasError = !string.IsNullOrWhiteSpace(vm.ExistingCampaignGiftsStatus)
                               && !string.Equals(vm.ExistingCampaignGiftsStatus.Trim(), "No gifts found.", StringComparison.OrdinalIgnoreCase);

                if (!hasError && vm.ExistingCampaignGifts.Count == 0)
                    GiftHistoryExpander.IsExpanded = false;

                applied = true;
                Unhook();
            }

            void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(GiftEntryViewModel.IsLoadingExistingCampaignGifts)
                    || e.PropertyName == nameof(GiftEntryViewModel.ExistingCampaignGiftsStatus))
                {
                    TryApply();
                }
            }

            void ExistingCampaignGifts_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
            {
                TryApply();
            }

            vm.PropertyChanged += Vm_PropertyChanged;
            vm.ExistingCampaignGifts.CollectionChanged += ExistingCampaignGifts_CollectionChanged;

            // In case gifts were already loaded before the window finished constructing.
            TryApply();
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
                // Query the donor's contributed funds from RE NXT (via SKY API) once,
                // then evaluate both:
                //  - New Radio Constituent (based on RadioFunds tokens)
                //  - First Time Giver (based on SQL fund exclusions for the active campaign)
                var funds = await App.ConstituentService.GetContributedFundsAsync(_row.Id, maxGiftsToScan: 1000);

                // 1) New Radio Constituent
                var radioTokens = LoadRadioFundTokens();
                if (radioTokens.Length == 0)
                {
                    _isNewRadioConstituent = false;
                }
                else
                {
                    var hasRadioFundGift = funds.Any(f => radioTokens.Any(t => TokenMatches(t, f.Name)));
                    _isNewRadioConstituent = !hasRadioFundGift;
                }

                // 2) First Time Giver (respects dbo.CGFirstTimeFundExclusions for the active campaign)
                var excludedTokens = await App.FirstTimeGiverRules.GetExcludedFundTokensAsync();
                if (funds.Count == 0)
                {
                    _isFirstTimeGiver = true;
                }
                else if (excludedTokens.Length == 0)
                {
                    _isFirstTimeGiver = false;
                }
                else
                {
                    var hasCountedGift = funds.Any(f => !excludedTokens.Any(t => TokenMatches(t, f.Name)));
                    _isFirstTimeGiver = !hasCountedGift;
                }
            }
            catch
            {
                _isNewRadioConstituent = false;
                _isFirstTimeGiver = false;
            }
            finally
            {
                // Persist into workflow so local SQL includes it.
                _workflow.IsNewRadioConstituent = _isNewRadioConstituent;
                _workflow.IsFirstTimeGiver = _isFirstTimeGiver;

                ApplyBanner();
                Mouse.OverrideCursor = null;
                Cursor = Cursors.Arrow;
            }
        }

        private void ApplyBanner()
        {
            if (NewConstituentBanner is not null)
                NewConstituentBanner.Visibility = _isNewRadioConstituent ? Visibility.Visible : Visibility.Collapsed;

            if (FirstTimeGiverBanner is not null)
                FirstTimeGiverBanner.Visibility = _isFirstTimeGiver ? Visibility.Visible : Visibility.Collapsed;
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