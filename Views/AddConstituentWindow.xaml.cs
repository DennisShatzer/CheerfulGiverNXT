using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CheerfulGiverNXT.Infrastructure.Ui;

namespace CheerfulGiverNXT
{
    public partial class AddConstituentWindow : Window
    {
        public DraftConstituent Draft { get; }

        public string DraftDisplayName => Draft.DisplayName;

        public int? CreatedConstituentId { get; private set; }

        public AddConstituentWindow(string? initialSearchText = null)
        {
            InitializeComponent();

            Draft = new DraftConstituent();
            DataContext = Draft;

            // Simple prefill: split "First Last" (best effort).
            if (!string.IsNullOrWhiteSpace(initialSearchText))
            {
                Draft.OrganizationName = initialSearchText.Trim();

                var parts = initialSearchText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 1)
                {
                    Draft.LastName = parts[0];
                }
                else if (parts.Length >= 2)
                {
                    Draft.FirstName = parts[0];
                    Draft.LastName = string.Join(' ', parts, 1, parts.Length - 1);
                }
            }

            Loaded += (_, __) => FocusNameField();
        }

        private void ConstituentType_Checked(object sender, RoutedEventArgs e)
        {
            // Keep this simple and operator-friendly.
            FocusNameField();
        }

        private void FocusNameField()
        {
            if (Draft.IsOrganization)
            {
                OrganizationNameTextBox?.Focus();
            }
            else
            {
                FirstNameTextBox?.Focus();
            }
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            // Minimal validation.
            if (Draft.IsOrganization)
            {
                if (string.IsNullOrWhiteSpace(Draft.OrganizationName))
                {
                    MessageBox.Show("Please enter an organization name.", "Missing name", MessageBoxButton.OK, MessageBoxImage.Warning);
                    OrganizationNameTextBox.Focus();
                    return;
                }
            }
            else
            {
                // SKY requires a last name for Individuals.
                if (string.IsNullOrWhiteSpace(Draft.LastName))
                {
                    MessageBox.Show("Please enter a last name.", "Missing name", MessageBoxButton.OK, MessageBoxImage.Warning);
                    LastNameTextBox.Focus();
                    return;
                }
            }

            SaveButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                var created = Draft.IsOrganization
                    ? await App.ConstituentService.CreateOrganizationConstituentAsync(
                        Draft.OrganizationName,
                        Draft.Email,
                        Draft.Phone,
                        Draft.AddressLine1,
                        Draft.AddressLine2,
                        Draft.City,
                        Draft.State,
                        Draft.PostalCode)
                    : await App.ConstituentService.CreateIndividualConstituentAsync(
                        Draft.FirstName,
                        Draft.LastName,
                        Draft.Email,
                        Draft.Phone,
                        Draft.AddressLine1,
                        Draft.AddressLine2,
                        Draft.City,
                        Draft.State,
                        Draft.PostalCode);

                CreatedConstituentId = created.Id;

                if (created.Warnings.Count > 0)
                {
                    MessageBox.Show(
                        $"Created Constituent ID {created.Id}.\n\nHowever, some contact details could not be saved:\n\n- " +
                        string.Join("\n- ", created.Warnings),
                        "Created with warnings",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                UiError.Show(ex, title: "Create failed", context: "AddConstituentWindow.Save_Click", message: "Unable to create the constituent.", owner: this);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                SaveButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        public sealed class DraftConstituent : INotifyPropertyChanged
        {
            public enum ConstituentKind
            {
                Individual,
                Organization
            }

            private ConstituentKind _kind = ConstituentKind.Individual;
            public ConstituentKind Kind
            {
                get => _kind;
                set
                {
                    if (_kind == value) return;
                    _kind = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsIndividual));
                    OnPropertyChanged(nameof(IsOrganization));
                    OnPropertyChanged(nameof(DisplayName));
                }
            }

            public bool IsIndividual
            {
                get => Kind == ConstituentKind.Individual;
                set
                {
                    if (value) Kind = ConstituentKind.Individual;
                }
            }

            public bool IsOrganization
            {
                get => Kind == ConstituentKind.Organization;
                set
                {
                    if (value) Kind = ConstituentKind.Organization;
                }
            }

            private string _firstName = "";
            public string FirstName
            {
                get => _firstName;
                set { _firstName = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
            }

            private string _lastName = "";
            public string LastName
            {
                get => _lastName;
                set { _lastName = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
            }

            private string _organizationName = "";
            public string OrganizationName
            {
                get => _organizationName;
                set { _organizationName = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
            }

            private string _email = "";
            public string Email
            {
                get => _email;
                set { _email = value; OnPropertyChanged(); }
            }

            private string _phone = "";
            public string Phone
            {
                get => _phone;
                set { _phone = value; OnPropertyChanged(); }
            }

            private string _addressLine1 = "";
            public string AddressLine1
            {
                get => _addressLine1;
                set { _addressLine1 = value; OnPropertyChanged(); }
            }

            private string _addressLine2 = "";
            public string AddressLine2
            {
                get => _addressLine2;
                set { _addressLine2 = value; OnPropertyChanged(); }
            }

            private string _city = "";
            public string City
            {
                get => _city;
                set { _city = value; OnPropertyChanged(); }
            }

            private string _state = "";
            public string State
            {
                get => _state;
                set { _state = value; OnPropertyChanged(); }
            }

            private string _postalCode = "";
            public string PostalCode
            {
                get => _postalCode;
                set { _postalCode = value; OnPropertyChanged(); }
            }

            private string _notes = "";
            public string Notes
            {
                get => _notes;
                set { _notes = value; OnPropertyChanged(); }
            }

            public string DisplayName
            {
                get
                {
                    var full = IsOrganization
                        ? (OrganizationName ?? "").Trim()
                        : ($"{FirstName} {LastName}").Trim();

                    return string.IsNullOrWhiteSpace(full) ? "(no name)" : full;
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            private void OnPropertyChanged([CallerMemberName] string? name = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
