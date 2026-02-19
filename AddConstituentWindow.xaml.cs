using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace CheerfulGiverNXT
{
    public partial class AddConstituentWindow : Window
    {
        public DraftConstituent Draft { get; }

        public string DraftDisplayName => Draft.DisplayName;

        public AddConstituentWindow(string? initialSearchText = null)
        {
            InitializeComponent();

            Draft = new DraftConstituent();
            DataContext = Draft;

            // Simple prefill: split "First Last" (best effort).
            if (!string.IsNullOrWhiteSpace(initialSearchText))
            {
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

            Loaded += (_, __) => FirstNameTextBox.Focus();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // Minimal validation: need something name-like.
            if (string.IsNullOrWhiteSpace(Draft.FirstName) && string.IsNullOrWhiteSpace(Draft.LastName))
            {
                MessageBox.Show("Please enter at least a first or last name.", "Missing name", MessageBoxButton.OK, MessageBoxImage.Warning);
                FirstNameTextBox.Focus();
                return;
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        public sealed class DraftConstituent : INotifyPropertyChanged
        {
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
                    var full = ($"{FirstName} {LastName}").Trim();
                    return string.IsNullOrWhiteSpace(full) ? "(no name)" : full;
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            private void OnPropertyChanged([CallerMemberName] string? name = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
