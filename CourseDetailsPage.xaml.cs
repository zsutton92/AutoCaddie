using AutoCaddie.Models;
using AutoCaddie.Services;
using System.Text.Json;

namespace AutoCaddie;

public partial class CourseDetailsPage : ContentPage
{
    private string _courseName;
    private DatabaseService _dbService = new();

    public CourseDetailsPage(string courseName)
    {
        InitializeComponent();
        _courseName = courseName;
        CourseNameLabel.Text = _courseName;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadCourseHistory();
        await FetchWeatherAsync();
    }

    private async Task LoadCourseHistory()
    {
        var allRounds = await _dbService.GetAllRoundsAsync();
        var courseRounds = allRounds.Where(r => r.CourseName == _courseName).ToList();
        PastRoundsList.ItemsSource = courseRounds;
    }

    private async Task FetchWeatherAsync()
    {
        try
        {
            var holes = await _dbService.GetHolesForCourseAsync(_courseName);
            if (holes == null || holes.Count == 0) return;

            // Use the 1st hole's tee box to ping the weather API
            double lat = holes.First().TeeLatitude;
            double lon = holes.First().TeeLongitude;

            using HttpClient client = new HttpClient();
            string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code,wind_speed_10m&temperature_unit=fahrenheit&wind_speed_unit=mph";

            var response = await client.GetStringAsync(url);
            var weatherData = JsonSerializer.Deserialize<OpenMeteoResponse>(response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (weatherData?.current != null)
            {
                TempLabel.Text = $"{Math.Round(weatherData.current.temperature_2m)}°F";
                WindLabel.Text = $"{Math.Round(weatherData.current.wind_speed_10m)} mph";

                SetWeatherCondition(weatherData.current.weather_code);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Weather fetch failed: {ex.Message}");
        }
    }

    private void SetWeatherCondition(int code)
    {
        // WMO Weather interpretation codes
        if (code == 0) { ConditionLabel.Text = "Clear"; WeatherIconLabel.Text = "☀️"; }
        else if (code <= 3) { ConditionLabel.Text = "Cloudy"; WeatherIconLabel.Text = "⛅"; }
        else if (code == 45 || code == 48) { ConditionLabel.Text = "Fog"; WeatherIconLabel.Text = "🌫️"; }
        else if (code >= 51 && code <= 67) { ConditionLabel.Text = "Rain"; WeatherIconLabel.Text = "🌧️"; }
        else if (code >= 71 && code <= 77) { ConditionLabel.Text = "Snow"; WeatherIconLabel.Text = "❄️"; }
        else if (code >= 95) { ConditionLabel.Text = "Storm"; WeatherIconLabel.Text = "⛈️"; }
    }

    private async void OnPreview_Clicked(object sender, EventArgs e)
    {
        // Launch in preview mode (no GPS, no tees)
        await Navigation.PushAsync(new CoursePage(_courseName, null, true));
    }

    private async void OnStartRound_Clicked(object sender, EventArgs e)
    {
        // Prompt for tees exactly like we used to do on the MainPage
        var availableTees = await _dbService.GetAvailableTeesAsync(_courseName);
        string selectedTeeColor = null;

        if (availableTees != null && availableTees.Count > 0)
        {
            selectedTeeColor = await DisplayActionSheet("Select Tee Box", "Cancel", null, availableTees.ToArray());
            if (string.IsNullOrEmpty(selectedTeeColor) || selectedTeeColor == "Cancel")
            {
                return;
            }
        }

        // Launch an active tracking round
        await Navigation.PushAsync(new CoursePage(_courseName, selectedTeeColor, false));
    }

    // JSON Classes for Open-Meteo
    public class OpenMeteoResponse { public CurrentWeather current { get; set; } }
    public class CurrentWeather { public double temperature_2m { get; set; } public int weather_code { get; set; } public double wind_speed_10m { get; set; } }
}