using AutoCaddie.Models;
using AutoCaddie.Services;
using System.Text.Json;

namespace AutoCaddie;

public partial class CourseDetailsPage : ContentPage
{
    private string _courseName;
    private DatabaseService _dbService = new();
    private CourseWeather _currentWeather = new CourseWeather();

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

            double lat = holes.First().TeeLatitude;
            double lon = holes.First().TeeLongitude;

            using HttpClient client = new HttpClient();
            string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&temperature_unit=fahrenheit&wind_speed_unit=mph";

            var response = await client.GetStringAsync(url);
            var weatherData = JsonSerializer.Deserialize<OpenMeteoResponse>(response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (weatherData?.current != null)
            {
                // Populate the object
                _currentWeather.WindSpeedMph = weatherData.current.wind_speed_10m;
                _currentWeather.WindDirectionDegrees = weatherData.current.wind_direction_10m;
                _currentWeather.Temperature = weatherData.current.temperature_2m;
                _currentWeather.Condition = GetConditionString(weatherData.current.weather_code);
                _currentWeather.LastUpdated = DateTime.Now;

                TempLabel.Text = $"{Math.Round(_currentWeather.Temperature)}°F";
                WindLabel.Text = $"{Math.Round(_currentWeather.WindSpeedMph)} mph";

                // Update UI with the condition string
                SetWeatherCondition(_currentWeather.Condition);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Weather fetch failed: {ex.Message}");
        }
    }

    private string GetConditionString(int code)
    {
        // Retaining your specific names (Fog, Cloudy, etc.)
        if (code == 0) return "Clear";
        if (code <= 3) return "Cloudy";
        if (code == 45 || code == 48) return "Fog";
        if (code >= 51 && code <= 67) return "Rain";
        if (code >= 71 && code <= 77) return "Snow";
        if (code >= 95) return "Storm";
        return "Clear";
    }

    private void SetWeatherCondition(string condition)
    {
        ConditionLabel.Text = condition;

        // Emoji logic matched to the condition string
        if (condition == "Clear") WeatherIconLabel.Text = "☀️";
        else if (condition == "Cloudy") WeatherIconLabel.Text = "⛅";
        else if (condition == "Fog") WeatherIconLabel.Text = "🌫️";
        else if (condition == "Rain") WeatherIconLabel.Text = "🌧️";
        else if (condition == "Snow") WeatherIconLabel.Text = "❄️";
        else if (condition == "Storm") WeatherIconLabel.Text = "⛈️";
        else WeatherIconLabel.Text = "☀️";
    }

    private async void OnPreview_Clicked(object sender, EventArgs e)
    {
        // ADDED: Passing _currentWeather to the CoursePage
        await Navigation.PushAsync(new CoursePage(_courseName, null, true, _currentWeather));
    }

    private async void OnStartRound_Clicked(object sender, EventArgs e)
    {
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

        // ADDED: Passing _currentWeather to the CoursePage
        await Navigation.PushAsync(new CoursePage(_courseName, selectedTeeColor, false, _currentWeather));
    }

    // JSON Classes for Open-Meteo
    public class OpenMeteoResponse { public CurrentWeather current { get; set; } }

    // ADDED: wind_direction_10m property so the parser doesn't crash
    public class CurrentWeather
    {
        public double temperature_2m { get; set; }
        public int weather_code { get; set; }
        public double wind_speed_10m { get; set; }
        public double wind_direction_10m { get; set; }
    }
}