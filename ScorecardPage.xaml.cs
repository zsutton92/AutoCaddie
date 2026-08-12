using AutoCaddie.Models;
using AutoCaddie.Services;
using System.Collections.ObjectModel;

namespace AutoCaddie;

public partial class ScorecardPage : ContentPage
{
    private GolfRound _round;
    private DatabaseService _dbService = new();
    public ObservableCollection<HoleScore> HoleScores { get; set; } = new();

    public ScorecardPage(GolfRound round)
    {
        InitializeComponent();
        _round = round;
        ScoresList.ItemsSource = HoleScores;

        LoadScorecard();
    }

    private async void LoadScorecard()
    {
        CourseNameLabel.Text = _round.CourseName;
        DateLabel.Text = _round.DatePlayed.ToString("MMMM dd, yyyy");
        TotalScoreLabel.Text = _round.TotalScore.ToString();

        // Calculate Relation to Par (e.g. +4, E, -2)
        int toPar = _round.TotalScore - _round.TotalPar;
        ToParLabel.Text = toPar > 0 ? $"+{toPar}" : (toPar == 0 ? "E" : toPar.ToString());

        var scores = await _dbService.GetScoresForRoundAsync(_round.Id);
        foreach (var s in scores)
        {
            HoleScores.Add(s);
        }
    }
}