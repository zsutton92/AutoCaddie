using AutoCaddie.Models;
using Microsoft.Maui.ApplicationModel;

namespace AutoCaddie
{
    public partial class MyBagPage : ContentPage
    {
        private Services.DatabaseService _dbService;

        public MyBagPage()
        {
            InitializeComponent();
            _dbService = new Services.DatabaseService();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // Load the Training Mode state
            // Note: We use "DisableTrainingMode" as the key, so if it's true, the switch is OFF
            bool isManuallyDisabled = Preferences.Default.Get("DisableTrainingMode", false);
            TrainingModeSwitch.IsToggled = !isManuallyDisabled;

            await LoadBagAsync();
        }

        private async Task LoadBagAsync()
        {
            var myBag = await _dbService.GetMyBagAsync();

            // Sort by longest hitting clubs first
            BagListView.ItemsSource = myBag.OrderByDescending(c => c.AverageYardage).ToList();
        }

        private void OnTrainingMode_Toggled(object sender, ToggledEventArgs e)
        {
            // If the switch is ON, DisableTrainingMode is false.
            Preferences.Default.Set("DisableTrainingMode", !e.Value);
        }

        private async void OnAddClub_Clicked(object sender, EventArgs e)
        {
            string name = ClubNameEntry.Text?.Trim();
            string distanceText = ClubDistanceEntry.Text?.Trim();

            if (string.IsNullOrEmpty(name))
            {
                await DisplayAlert("Error", "Please enter a club name.", "OK");
                return;
            }

            double startingYardage = 0;
            double.TryParse(distanceText, out startingYardage);

            var newClub = new AutoCaddie.Models.GolfClub
            {
                Name = name,
                // We seed the total yardage and shot count as 1 so the average math works immediately
                TotalYardage = startingYardage,
                ShotCount = startingYardage > 0 ? 1 : 0
            };

            await _dbService.SaveClubAsync(newClub);

            // Clear the form and refresh the list
            ClubNameEntry.Text = string.Empty;
            ClubDistanceEntry.Text = string.Empty;
            await LoadBagAsync();
        }
        private async void OnEditClub_Clicked(object sender, EventArgs e)
        {
            // Check if the sender is the Border or the GestureRecognizer and extract the CommandParameter safely
            AutoCaddie.Models.GolfClub clubToEdit = null;

            if (sender is Border border && border.GestureRecognizers.FirstOrDefault() is TapGestureRecognizer tap)
            {
                clubToEdit = tap.CommandParameter as AutoCaddie.Models.GolfClub;
            }
            else if (sender is TapGestureRecognizer gesture)
            {
                clubToEdit = gesture.CommandParameter as AutoCaddie.Models.GolfClub;
            }

            if (clubToEdit != null)
            {
                string result = await DisplayPromptAsync(
                    "Edit Yardage",
                    $"Enter your new true yardage for the {clubToEdit.Name}:",
                    initialValue: Math.Round(clubToEdit.AverageYardage).ToString(),
                    keyboard: Keyboard.Numeric);

                if (!string.IsNullOrWhiteSpace(result) && double.TryParse(result, out double newYardage))
                {
                    clubToEdit.TotalYardage = newYardage;
                    clubToEdit.ShotCount = 1;

                    await _dbService.SaveClubAsync(clubToEdit);
                    await LoadBagAsync();
                }
            }
        }

        private async void OnDeleteClub_Clicked(object sender, EventArgs e)
        {
            AutoCaddie.Models.GolfClub clubToDelete = null;

            if (sender is Border border && border.GestureRecognizers.FirstOrDefault() is TapGestureRecognizer tap)
            {
                clubToDelete = tap.CommandParameter as AutoCaddie.Models.GolfClub;
            }
            else if (sender is TapGestureRecognizer gesture)
            {
                clubToDelete = gesture.CommandParameter as AutoCaddie.Models.GolfClub;
            }

            if (clubToDelete != null)
            {
                bool confirm = await DisplayAlert("Remove Club", $"Are you sure you want to remove your {clubToDelete.Name}?", "Yes", "Cancel");
                if (confirm)
                {
                    await _dbService.DeleteClubAsync(clubToDelete);
                    await LoadBagAsync();
                }
            }
        }
    }
}