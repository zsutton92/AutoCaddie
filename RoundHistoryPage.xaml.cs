using AutoCaddie.Models;
using AutoCaddie.Services;
using System.Collections.ObjectModel;

namespace AutoCaddie;

public partial class RoundHistoryPage : ContentPage
{
    private DatabaseService _dbService = new();
    public ObservableCollection<GolfRound> Rounds { get; set; } = new();

    public RoundHistoryPage()
    {
        InitializeComponent();
        HistoryList.ItemsSource = Rounds;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Rounds.Clear();

        var pastRounds = await _dbService.GetAllRoundsAsync();
        foreach (var r in pastRounds)
        {
            Rounds.Add(r);
        }
    }

    private async void OnRoundSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is GolfRound selectedRound)
        {
            HistoryList.SelectedItem = null;
            // Push to the detailed Scorecard Page
            await Navigation.PushAsync(new ScorecardPage(selectedRound));
        }
    }
}