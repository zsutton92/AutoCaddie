using AutoCaddie.Models;
using AutoCaddie.Services;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.ApplicationModel;

namespace AutoCaddie;

public partial class CoursePage : ContentPage
{
    private string _courseName;
    private DatabaseService _dbService = new();
    private List<GolfHole> _holes = new();
    private int _currentIndex = 0;
    private bool _isTracking = false;
    private double _lastShotDistance = 0;
    private bool _isTrainingMode = false;

    private Location _lastKnownLocation;
    private Location _lastAnnouncedLocation;
    private IDispatcherTimer _announceTimer;
    private bool _isMuted = false;
    private Dictionary<int, int> _roundScores = new();
    private double _pendingShotDistance = 0;
    private List<GolfClub> _myBag = new();
    private SpeechOptions _caddieVoice;


    public CoursePage(string courseName)
    {
        InitializeComponent();

        InjectFakeTestData(); // For testing purposes, remove in production
        _courseName = courseName;

        _announceTimer = Dispatcher.CreateTimer();
        _announceTimer.Interval = TimeSpan.FromSeconds(5);
        _announceTimer.Tick += OnAnnounceTimer_Tick;

        CourseMap.Loaded += async (s, e) =>
        {
            await LoadCourseData();
            _myBag = await _dbService.GetMyBagAsync();
            await InitializeCaddieVoice();
            await StartGpsTracking();
        };
    }
    private void UpdateClubSuggestion(double targetYards)
    {
        if (_myBag == null || _myBag.Count == 0) return;

        // Filter out clubs that don't have any saved shots yet
        var activeClubs = _myBag.Where(c => c.AverageYardage > 0).ToList();

        if (activeClubs.Count == 0)
        {
            SuggestionLabel.Text = "Caddie: Hit shots to learn!";
            return;
        }

        // Rule 1: Short game (Inside 30 yards)
        if (targetYards <= 30)
        {
            SuggestionLabel.Text = "Caddie: Pitch / Chip";
            return;
        }

        // Rule 2: Find the longest club
        var longestClub = activeClubs.OrderByDescending(c => c.AverageYardage).First();

        // If the hole is way longer than your furthest club, just hit the big stick
        if (targetYards >= longestClub.AverageYardage + 15)
        {
            SuggestionLabel.Text = $"Caddie: Layup ({longestClub.Name})";
            return;
        }

        // Rule 3: Find the closest club
        var closestClub = activeClubs.OrderBy(c => Math.Abs(c.AverageYardage - targetYards)).First();
        SuggestionLabel.Text = $"Caddie: {closestClub.Name}";
    }
    private async void OnAnnounceTimer_Tick(object sender, EventArgs e)
    {
        _announceTimer.Stop();
        double shotDistanceToLog = _lastShotDistance;
        if (_isMuted) return;


        if (YardageLabel.Text != "-- YDS")
        {
            string caddieCompliment = "";
            Random rnd = new Random();

            if (_isTrainingMode && shotDistanceToLog > 10)
            {
                _pendingShotDistance = Math.Round(shotDistanceToLog);
                ClubDistanceLabel.Text = $"{_pendingShotDistance} Yards";

                // Load the bag and bind it to the FlexLayout if we haven't yet
                if (_myBag.Count == 0)
                {
                    _myBag = await _dbService.GetMyBagAsync();
                    BindableLayout.SetItemsSource(ClubFlexLayout, _myBag);
                }

                // Show the custom popup!
                ClubSelectionOverlay.IsVisible = true;
            }

            // If the last shot traveled further than 250 yards
            if (_lastShotDistance >= 250)
            {
                string[] bigDrives = { "Wow, absolute nuke!", "Killer drive!", "Smashed it!", "Great shot!" };
                caddieCompliment = bigDrives[rnd.Next(bigDrives.Length)] + " ";
            }
            // If the last shot traveled between 150 and 249 yards
            else if (_lastShotDistance >= 150)
            {
                string[] solidShots = { "Nice strike!", "Solid shot!", "Looking good!" };
                caddieCompliment = solidShots[rnd.Next(solidShots.Length)] + " ";
            }

            // Combine the compliment with the yardage
            string yardageSpeech = YardageLabel.Text.Replace("YDS", "yards to the center");
            string caddieSpeech = GetSpokenCaddieSuggestion();
            string finalSpeech = $"{caddieCompliment}{yardageSpeech}{caddieSpeech}";

            //var voiceOptions = await GetCaddieVoiceOptions();
            await TextToSpeech.Default.SpeakAsync(finalSpeech, _caddieVoice);

            _lastAnnouncedLocation = _lastKnownLocation;
            _lastShotDistance = 0; // Reset for the next shot
        }
    }

    private async Task InitializeCaddieVoice()
    {
        var locales = await TextToSpeech.Default.GetLocalesAsync();

        // Find the British voice
        var caddieLocale = locales.FirstOrDefault(l => l.Language.ToLower() == "en" && l.Country.ToLower() == "gb");

        _caddieVoice = new SpeechOptions
        {
            Locale = caddieLocale,
            Pitch = 0.9f,
            Volume = 1.0f
        };
    }
    private async Task LoadCourseData()
    {
        var rawHoles = await _dbService.GetHolesForCourseAsync(_courseName);

        _holes = rawHoles.GroupBy(h => h.HoleNumber)
                         .Select(g => g.First())
                         .OrderBy(h => h.HoleNumber)
                         .ToList();

        if (_holes.Count > 0)
        {
            // Call our async display method
            await DisplayCurrentHoleAsync();
        }
        else
        {
            await DisplayAlert("Notice", "No hole data found for this course.", "OK");
        }
    }

    private double CalculateBearing(double lat1, double lon1, double lat2, double lon2)
    {
        double dLon = (lon2 - lon1) * (Math.PI / 180.0);
        lat1 = lat1 * (Math.PI / 180.0);
        lat2 = lat2 * (Math.PI / 180.0);

        double y = Math.Sin(dLon) * Math.Cos(lat2);
        double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);

        double bearing = Math.Atan2(y, x) * (180.0 / Math.PI);
        return (bearing + 360) % 360;
    }

    // UPDATED: Now strictly uses DP (Density Independent Pixels) for accurate Google Maps math
    private float CalculatePerfectZoom(double midLat, double distanceMeters, double mapHeightDp)
    {
        // 50 DP of padding to ensure the flag and tee easily clear the screen edges
        double paddingDp = 50;
        double availableDp = mapHeightDp - paddingDp;
        if (availableDp <= 0) availableDp = 500; // Safe fallback if UI isn't fully drawn

        // Add a 25% physical distance buffer so it frames comfortably
        double bufferedDistance = distanceMeters * 1.25;

        // Earth circumference map projection formula
        double equatorLength = 40075016.686;
        double metersPerDpAtZoom0 = equatorLength / 256.0;
        double latRadians = midLat * (Math.PI / 180.0);

        double requiredMetersPerDp = bufferedDistance / availableDp;

        // Log base 2 calculates the exact zoom level
        double zoom = Math.Log2((metersPerDpAtZoom0 * Math.Cos(latRadians)) / requiredMetersPerDp);

        // Clamp to a reasonable max/min zoom for golf
        if (zoom > 20f) zoom = 20f;
        if (zoom < 14f) zoom = 14f;

        return (float)zoom;
    }

    private async Task DisplayCurrentHoleAsync()
    {
        if (_holes.Count == 0) return;

        var currentHole = _holes[_currentIndex];

        HoleTitleLabel.Text = $"Hole {currentHole.HoleNumber}";
        ParLabel.Text = $"Par {currentHole.Par}";

        if (!_roundScores.ContainsKey(currentHole.HoleNumber))
        {
            _roundScores[currentHole.HoleNumber] = currentHole.Par;
        }

        // Update the UI Label
        ScoreLabel.Text = _roundScores[currentHole.HoleNumber].ToString();

        if (_lastKnownLocation != null)
        {
            UpdateYardages(_lastKnownLocation);
        }
        else
        {
            // Reset to dashes if GPS hasn't found us yet
            Dispatcher.Dispatch(() =>
            {
                BackYardageLabel.Text = "-- B";
                YardageLabel.Text = "-- YDS";
                FrontYardageLabel.Text = "-- F";
            });
        }

        CourseMap.Pins.Clear();

       

        double midLat = (currentHole.TeeLatitude + currentHole.GreenLatitude) / 2;
        double midLon = (currentHole.TeeLongitude + currentHole.GreenLongitude) / 2;
        double bearing = CalculateBearing(currentHole.TeeLatitude, currentHole.TeeLongitude, currentHole.GreenLatitude, currentHole.GreenLongitude);

        double distanceMeters = Location.CalculateDistance(currentHole.TeeLatitude, currentHole.TeeLongitude, currentHole.GreenLatitude, currentHole.GreenLongitude, DistanceUnits.Kilometers) * 1000.0;
        if (distanceMeters < 50) distanceMeters = 50;

        Dispatcher.Dispatch(() =>
        {
#if ANDROID
            var mapHandler = CourseMap.Handler as Microsoft.Maui.Maps.Handlers.MapHandler;
            if (mapHandler?.Map is Android.Gms.Maps.GoogleMap googleMap)
            {
                googleMap.Clear();

                googleMap.UiSettings.MyLocationButtonEnabled = false;
                googleMap.UiSettings.ZoomControlsEnabled = false;
                googleMap.UiSettings.CompassEnabled = false;
                googleMap.UiSettings.MapToolbarEnabled = false;

                var teePoint = new Android.Gms.Maps.Model.LatLng(currentHole.TeeLatitude, currentHole.TeeLongitude);
                var greenPoint = new Android.Gms.Maps.Model.LatLng(currentHole.GreenLatitude, currentHole.GreenLongitude);

                var context = Android.App.Application.Context;

                // 1. Calculate the zoom using MAUI's raw Height property (already in DP)
                float perfectZoom = CalculatePerfectZoom(midLat, distanceMeters, CourseMap.Height);

                // 2. Package the Target, Zoom, and Bearing into ONE seamless instruction
                var cinematicCamera = new Android.Gms.Maps.Model.CameraPosition.Builder()
                    .Target(new Android.Gms.Maps.Model.LatLng(midLat, midLon))
                    .Zoom(perfectZoom)
                    .Bearing((float)bearing)
                    .Build();

                // 3. Smooth animation
                googleMap.AnimateCamera(Android.Gms.Maps.CameraUpdateFactory.NewCameraPosition(cinematicCamera));

                int teeId = context.Resources.GetIdentifier("tee", "drawable", context.PackageName);
                var teeOptions = new Android.Gms.Maps.Model.MarkerOptions()
                    .SetPosition(teePoint)
                    .SetTitle("Tee")
                    .Anchor(0.5f, 0.5f);

                if (teeId != 0) teeOptions.SetIcon(Android.Gms.Maps.Model.BitmapDescriptorFactory.FromResource(teeId));
                googleMap.AddMarker(teeOptions);

                int flagId = context.Resources.GetIdentifier("flag", "drawable", context.PackageName);
                var flagOptions = new Android.Gms.Maps.Model.MarkerOptions()
                    .SetPosition(greenPoint)
                    .SetTitle("Green")
                    .Anchor(0.5f, 1.0f);

                if (flagId != 0) flagOptions.SetIcon(Android.Gms.Maps.Model.BitmapDescriptorFactory.FromResource(flagId));
                googleMap.AddMarker(flagOptions);
            }
#else
            var centerPosition = new Location(midLat, midLon);
            CourseMap.MoveToRegion(MapSpan.FromCenterAndRadius(centerPosition, Distance.FromMeters(250)));
#endif
        });
    }

    private async Task StartGpsTracking()
    {
        // 1. Check and Request Location Permissions
        var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        }

        if (status == PermissionStatus.Granted)
        {
            // 2. Turn on the native Google Maps Blue Dot!
            CourseMap.IsShowingUser = true;

            // 3. Start listening to live location updates
            Geolocation.LocationChanged += Geolocation_LocationChanged;
            var request = new GeolocationListeningRequest
            {
                DesiredAccuracy = GeolocationAccuracy.Best,
                MinimumTime = TimeSpan.FromSeconds(2) // Update every 2 seconds
            };

            await Geolocation.StartListeningForegroundAsync(request);
            _isTracking = true;

            _announceTimer.Start();
        }
    }
    private string GetSpokenCaddieSuggestion()
    {
        string suggestionText = SuggestionLabel.Text;

        // Don't read the suggestion if it's still learning or calculating
        if (suggestionText.Contains("Calculating") || suggestionText.Contains("Hit shots"))
        {
            return "";
        }

        // Strip out the "Caddie: " prefix
        string club = suggestionText.Replace("Caddie: ", "");

        // 1. Safe word replacements
        club = club.Replace("Dr", "Driver")
                   .Replace("Ptt", "Putter")
                   .Replace("PW", "Pitching Wedge")
                   .Replace("GW", "Gap Wedge")
                   .Replace("SW", "Sand Wedge")
                   .Replace("LW", "Lob Wedge");

        // 2. Safely translate Irons and Woods using a loop (1 through 9)
        for (int num = 1; num <= 9; num++)
        {
            club = club.Replace($"{num}i", $"{num} iron");
            club = club.Replace($"{num}w", $"{num} wood");
        }

        return $", I suggest {club}.";
    }

    //private void Geolocation_LocationChanged(object sender, GeolocationLocationChangedEventArgs e)
    //{
    //    if (_holes.Count == 0) return;

    //    var currentHole = _holes[_currentIndex];
    //    var myLocation = e.Location;

    //    // 1. Calculate physical distance from your phone to the Center of the Green
    //    double distanceMeters = Location.CalculateDistance(
    //        myLocation.Latitude, myLocation.Longitude,
    //        currentHole.GreenLatitude, currentHole.GreenLongitude,
    //        DistanceUnits.Kilometers) * 1000.0;

    //    // 2. Convert meters to Yards and round it to a clean whole number
    //    double centerYards = Math.Round(distanceMeters * 1.09361);

    //    // 3. Apply the 15-yard Front/Back smartwatch estimation
    //    double backYards = centerYards + 15;
    //    double frontYards = Math.Max(0, centerYards - 15); // Prevents negative yards if standing on the green

    //    // 4. Update the UI
    //    Dispatcher.Dispatch(() =>
    //    {
    //        BackYardageLabel.Text = $"{backYards} B";
    //        YardageLabel.Text = $"{centerYards} YDS";
    //        FrontYardageLabel.Text = $"{frontYards} F";
    //    });
    //}

    // NEW METHOD: Reusable yardage calculator
    private void UpdateYardages(Location myLocation)
    {
        if (_holes.Count == 0 || myLocation == null) return;

        var currentHole = _holes[_currentIndex];

        double distanceMeters = Location.CalculateDistance(
            myLocation.Latitude, myLocation.Longitude,
            currentHole.GreenLatitude, currentHole.GreenLongitude,
            DistanceUnits.Kilometers) * 1000.0;

        double centerYards = Math.Round(distanceMeters * 1.09361);
        double backYards = centerYards + 15;
        double frontYards = Math.Max(0, centerYards - 15);

        Dispatcher.Dispatch(() =>
        {
            BackYardageLabel.Text = $"{backYards} B";
            YardageLabel.Text = $"{centerYards} YDS";
            FrontYardageLabel.Text = $"{frontYards} F";

            UpdateClubSuggestion(centerYards);
        });
    }

    // UPDATED METHOD: Just saves the location and triggers the math
    //private void Geolocation_LocationChanged(object sender, GeolocationLocationChangedEventArgs e)
    //{
    //    _lastKnownLocation = e.Location;
    //    UpdateYardages(_lastKnownLocation);
    //}

    private void Geolocation_LocationChanged(object sender, GeolocationLocationChangedEventArgs e)
    {
        var myLocation = e.Location;

        double distanceSinceLastPing = _lastKnownLocation != null ?
            Location.CalculateDistance(_lastKnownLocation, myLocation, DistanceUnits.Kilometers) * 1093.61 : 0;

        _lastKnownLocation = myLocation;
        UpdateYardages(_lastKnownLocation);

        // NEW: Check if this is the start of a new hole
        bool isNewHole = _lastAnnouncedLocation == null;

        // Use 999 to force the timer to activate on a new hole, otherwise calculate normally
        double yardsFromLastAnnounce = isNewHole ? 999 :
            Location.CalculateDistance(_lastAnnouncedLocation, myLocation, DistanceUnits.Kilometers) * 1093.61;

        if (yardsFromLastAnnounce >= 10)
        {
            // Are we walking?
            if (distanceSinceLastPing > 1.5)
            {
                // ONLY log a shot distance if it's a real shot (not the fake 999 tee-box distance)
                if (!isNewHole)
                {
                    _lastShotDistance = yardsFromLastAnnounce;
                }

                // Keep delaying the voice until you stand still
                _announceTimer.Stop();
                _announceTimer.Start();
            }
        }
    }

    //private async void OnNextHole_Clicked(object sender, EventArgs e)
    //{
    //    if (_holes.Count == 0) return;
    //    _currentIndex = (_currentIndex + 1) % _holes.Count;
    //    await DisplayCurrentHoleAsync();
    //}

    //private async void OnPrevHole_Clicked(object sender, EventArgs e)
    //{
    //    if (_holes.Count == 0) return;
    //    _currentIndex = (_currentIndex - 1 + _holes.Count) % _holes.Count;
    //    await DisplayCurrentHoleAsync();
    //}

    private async void OnNextHole_Clicked(object sender, EventArgs e)
    {
        if (_holes.Count == 0) return;
        _currentIndex = (_currentIndex + 1) % _holes.Count;

        _lastAnnouncedLocation = null;
        _announceTimer.Stop();
        _announceTimer.Start(); // NEW: Kickstart the tee-box announcement

        await DisplayCurrentHoleAsync();
    }

    private async void OnPrevHole_Clicked(object sender, EventArgs e)
    {
        if (_holes.Count == 0) return;
        _currentIndex = (_currentIndex - 1 + _holes.Count) % _holes.Count;

        _lastAnnouncedLocation = null;
        _announceTimer.Stop();
        _announceTimer.Start(); // NEW: Kickstart the tee-box announcement

        await DisplayCurrentHoleAsync();
    }

    private async void OnYardageBadge_Tapped(object sender, TappedEventArgs e)
    {
        if (YardageLabel.Text != "-- YDS")
        {
            string speechText = YardageLabel.Text.Replace("YDS", "yards to the center");
            speechText += GetSpokenCaddieSuggestion();

            // Just pass the cached variable!
            await TextToSpeech.Default.SpeakAsync(speechText, _caddieVoice);
        }
    }


    private void OnPlusScore_Clicked(object sender, EventArgs e)
    {
        if (_holes.Count == 0) return;

        var currentHole = _holes[_currentIndex];

        // Max score of 15 (just in case it's a rough day out there)
        if (_roundScores[currentHole.HoleNumber] < 15)
        {
            _roundScores[currentHole.HoleNumber]++;
            ScoreLabel.Text = _roundScores[currentHole.HoleNumber].ToString();
        }
    }

    private void OnMinusScore_Clicked(object sender, EventArgs e)
    {
        if (_holes.Count == 0) return;

        var currentHole = _holes[_currentIndex];

        // Minimum score of 1 (Hole in one!)
        if (_roundScores[currentHole.HoleNumber] > 1)
        {
            _roundScores[currentHole.HoleNumber]--;
            ScoreLabel.Text = _roundScores[currentHole.HoleNumber].ToString();
        }
    }

    private void OnMuteButton_Clicked(object sender, EventArgs e)
    {
        _isMuted = !_isMuted;

        if (_isMuted)
        {
            MuteButton.Source = "speaker_off.png";
            _announceTimer.Stop(); // Kill any pending speech
        }
        else
        {
            MuteButton.Source = "speaker_on.png";
        }
    }

    private async void OnFinishRound_Clicked(object sender, EventArgs e)
    {
        // Ask for confirmation before ending the round
        bool confirm = await DisplayAlert("Finish Round", "Are you sure you want to save this scorecard and end the round?", "Yes, Save", "Keep Playing");

        if (confirm)
        {
            // Save to SQLite
            await _dbService.SaveCompletedRoundAsync(_courseName, _holes, _roundScores);

            // Turn off the GPS to save battery
            if (_isTracking)
            {
                Geolocation.LocationChanged -= Geolocation_LocationChanged;
                Geolocation.StopListeningForeground();
                _isTracking = false;
            }
            if (_announceTimer != null)
            {
                _announceTimer.Stop();
            }

            // Pop back to the main dashboard
            await Navigation.PopAsync();
        }
    }

    private async void OnClubSelected_Clicked(object sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is GolfClub selectedClub)
        {
            // Hide the overlay immediately for a snappy UX
            ClubSelectionOverlay.IsVisible = false;

            // Save the shot to the database
            await _dbService.SaveShotAsync(selectedClub.Id, _pendingShotDistance);
        }
    }

    private void OnSkipClub_Clicked(object sender, EventArgs e)
    {
        // Just dismiss the menu without logging anything
        ClubSelectionOverlay.IsVisible = false;
    }
    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        // Stop the GPS if the user leaves the page to save battery
        if (_isTracking)
        {
            Geolocation.LocationChanged -= Geolocation_LocationChanged;
            Geolocation.StopListeningForeground();
            _isTracking = false;
        }
    }

    private async void InjectFakeTestData()
    {
        var bag = await _dbService.GetMyBagAsync();

        foreach (var club in bag)
        {
            // Give each club 5 fake shots so the math works
            club.ShotCount = 5;

            if (club.Name == "Dr") club.TotalYardage = 240 * 5;
            if (club.Name == "3w") club.TotalYardage = 215 * 5;
            if (club.Name == "5i") club.TotalYardage = 175 * 5;
            if (club.Name == "7i") club.TotalYardage = 150 * 5;
            if (club.Name == "9i") club.TotalYardage = 125 * 5;
            if (club.Name == "PW") club.TotalYardage = 110 * 5;

            // You can leave the rest blank, the engine will just ignore clubs with 0 yards
        }

        // Save them to memory
        _myBag = bag;
    }
}