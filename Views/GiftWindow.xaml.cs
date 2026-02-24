using CheerfulGiverNXT.Services;
using CheerfulGiverNXT.ViewModels;
using CheerfulGiverNXT.Workflow;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CheerfulGiverNXT.Infrastructure.AppMode;

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

            // Reflect Demo mode in the title for safety (UI-only).
            ApplyModeUi();
            AppModeState.Instance.ModeChanged += (_, __) => ApplyModeUi();
        }

        private string? _baseTitle;

        private void ApplyModeUi()
        {
            try
            {
                _baseTitle ??= Title;
                Title = AppModeState.Instance.IsDemo ? (_baseTitle + " — DEMO") : _baseTitle;
            }
            catch
            {
                // UI-only
            }
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
                // Fund tokens are now configured per-campaign in Campaigns Admin (FundList).
                dialog = new CampaignsAdminWindow();
            }
            else if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift)
                     && (e.Key == Key.D || e.SystemKey == Key.D))
            {
                // Ctrl+Shift+D toggles Demo mode
                e.Handled = true;
                AppModeState.Instance.Toggle();
                ApplyModeUi();

                if (DataContext is GiftEntryViewModel vm)
                {
                    vm.StatusText = AppModeState.Instance.IsDemo
                        ? "DEMO mode enabled. Pledges will NOT be posted to SKY API."
                        : "LIVE mode enabled. Pledges will be posted to SKY API.";
                }

                return;
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
                // Determine:
                //  - New Radio Giver: new constituent OR existing constituent with no prior gifts to configured campaign FundList
                //  - First Time Giver: if any prior contributed fund matches ANY token in campaign FundList, they are NOT a first-time giver

                if (_workflow.IsNewConstituent)
                {
                    // Debug visibility (requested): show configured FundList and that SKY scan was skipped.
                    var fundListRaw = await App.FundListRules.GetFundListRawAsync();
                    // ShowFundListDebugMessage(fundListRaw, Array.Empty<string>(), skyScanNote: "(new constituent - SKY fund scan skipped)");

                    // If the operator just CREATED this constituent (not found in search), they are new by definition.
                    _isNewRadioConstituent = true;
                    _isFirstTimeGiver = true;
                    return;
                }

                // Existing constituent: query contributed funds once and evaluate both rules.
                var funds = await App.ConstituentService.GetContributedFundsAsync(_row.Id, maxGiftsToScan: 1000);

                // Debug visibility (requested): show FundList (local SQL) and funds returned by SKY.
                var fundListRawExisting = await App.FundListRules.GetFundListRawAsync();
                //ShowFundListDebugMessage(
                //    fundListRawExisting,
                //    funds.Select(f => f.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray(),
                //    skyScanNote: null);

                // Single source of truth: per-campaign semicolon-separated tokens in dbo.CGCampaigns.FundList.
                var tokens = await App.FundListRules.GetFundTokensAsync();
                if (tokens.Length == 0)
                {
                    // If not configured, default to false (safer than incorrectly flagging a donor).
                    // _isNewRadioConstituent = false;
                    _isFirstTimeGiver = false;
                }
                else
                {
                    var hasAnyMatch = funds.Any(f => tokens.Any(t => TokenMatches(t, f.Name)));
                    _isFirstTimeGiver = !hasAnyMatch;
                    //_isNewRadioConstituent = !hasAnyMatch;
                }
            }
            catch
            {
                //_isNewRadioConstituent = false;
                _isFirstTimeGiver = false;
            }
            finally
            {
                // Persist into workflow so local SQL includes it.
                //_workflow.IsNewRadioConstituent = _isNewRadioConstituent;
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

        //private void ShowFundListDebugMessage(string fundListRaw, string[] skyFundNames, string? skyScanNote)
        //{
        //    // Requested diagnostic message box: show campaign FundList (local SQL) and SKY API contributed funds.
        //    // Keep this resilient: never allow debug UI to break the gift workflow.
        //    try
        //    {
        //        fundListRaw = (fundListRaw ?? "").Trim();
        //        if (string.IsNullOrWhiteSpace(fundListRaw))
        //            fundListRaw = "(empty)";

        //        skyFundNames ??= Array.Empty<string>();
        //        var distinct = skyFundNames
        //            .Where(s => !string.IsNullOrWhiteSpace(s))
        //            .Select(s => s.Trim())
        //            .Distinct(StringComparer.OrdinalIgnoreCase)
        //            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
        //            .ToList();

        //        const int maxLines = 40;
        //        var shown = distinct.Take(maxLines).ToList();
        //        var extra = Math.Max(0, distinct.Count - shown.Count);

        //        var skyBlock = shown.Count == 0
        //            ? "(none)"
        //            : string.Join(Environment.NewLine, shown.Select(s => "• " + s));

        //        if (extra > 0)
        //            skyBlock += Environment.NewLine + $"… (+{extra} more)";

        //        var title = "FundList vs SKY Funds";
        //        var msg =
        //            $"Constituent: {_row.FullName} (ID: {_row.Id})\n\n" +
        //            $"FundList (Local SQL / CGCampaigns.FundList):\n{fundListRaw}\n\n" +
        //            $"SKY API contributed funds ({distinct.Count}):\n{skyBlock}";

        //        if (!string.IsNullOrWhiteSpace(skyScanNote))
        //            msg += "\n\n" + skyScanNote;

        //        // Ensure we're on the UI thread.
        //        if (Dispatcher.CheckAccess())
        //            MessageBox.Show(this, msg, title, MessageBoxButton.OK, MessageBoxImage.Information);
        //        else
        //            Dispatcher.Invoke(() => MessageBox.Show(this, msg, title, MessageBoxButton.OK, MessageBoxImage.Information));
        //    }
        //    catch
        //    {
        //        // swallow
        //    }
        //}



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