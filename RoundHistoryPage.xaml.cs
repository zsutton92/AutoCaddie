using AutoCaddie.Models;

namespace AutoCaddie
{
    public partial class RoundHistoryPage : ContentPage
    {
        private Services.DatabaseService _dbService;

        public RoundHistoryPage()
        {
            InitializeComponent();
            _dbService = new Services.DatabaseService();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await LoadHistoryAsync();
        }

        private async Task LoadHistoryAsync()
        {
            // Fetch all saved rounds, ordered by newest first
            var rounds = await _dbService.GetAllRoundsAsync();

            // BOUND TO XAML: Targets x:Name="HistoryList"
            HistoryList.ItemsSource = rounds;
        }

        private async void OnRoundSelected(object sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is GolfRound selectedRound)
            {
                // Fetch the 18-hole breakdown for this specific round
                var roundScores = await _dbService.GetHoleScoresAsync(selectedRound.Id);

                // Push to the dashboard, passing the summary and the hole data
                await Navigation.PushAsync(new ScorecardPage(selectedRound, roundScores));

                // Clear the selection so the card doesn't stay highlighted
                ((CollectionView)sender).SelectedItem = null;
            }
        }
    }
}