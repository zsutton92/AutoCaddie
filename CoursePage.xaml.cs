using AutoCaddie.Models;
using AutoCaddie.Services;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.ApplicationModel;

namespace AutoCaddie;

public partial class CoursePage : ContentPage
{
    private DatabaseService _dbService = new();
    private Location _lastKnownLocation;
    private Location _lastAnnouncedLocation;
    private List<GolfHole> _holes = new();
    private List<Location> _currentGreenPolygon = new();
    private List<GolfClub> _myBag = new();
    private Dictionary<int, int> _roundScores = new();
    private Dictionary<int, int> _roundPutts = new();
    private Dictionary<int, bool?> _roundFairways = new();

    private string _courseName;
    private string _selectedTeeColor;

    private bool _isTrainingMode = true;
    private bool _isPreviewMode;
    private bool _isTracking = false;
    private bool _isMuted = false;

    private int _currentIndex = 0;
    
    private double _lastShotDistance = 0;
    private double _pendingShotDistance = 0;
    private double _currentCenterYards = 0;


    private IDispatcherTimer _announceTimer; 
    private SpeechOptions _caddieVoice;
   
 

    public CoursePage(string courseName, string teeColor = null, bool isPreviewMode = false)
    {
        InitializeComponent();

       // InjectFakeTestData(); // For testing purposes, remove in production
        _courseName = courseName;
        _selectedTeeColor = teeColor;
        _isPreviewMode = isPreviewMode;
        ScoringPanel.IsVisible = !isPreviewMode;
        YardageBadge.IsVisible = !isPreviewMode;
        CaddieBadge.IsVisible = !isPreviewMode;
        this.Title = _courseName;
        MuteButton.IsVisible = !isPreviewMode;
        
        if (isPreviewMode)
        {
            this.ToolbarItems.Remove(FinishRoundButton);
        }

        CourseMap.Loaded += async (s, e) =>
        {
            await LoadCourseData();
            if (!_isPreviewMode)
            {
                
                _announceTimer = Dispatcher.CreateTimer();
                _announceTimer.Interval = TimeSpan.FromSeconds(5);
                _announceTimer.Tick += OnAnnounceTimer_Tick;
                _myBag = await _dbService.GetMyBagAsync();
                EvaluateTrainingMode();
                await InitializeCaddieVoice();
                await StartGpsTracking();
            }
        };
    }

    private void EvaluateTrainingMode()
    {
        // 1. Check if you manually forced it off in a settings page
        bool isManuallyDisabled = Preferences.Default.Get("DisableTrainingMode", false);
        if (isManuallyDisabled)
        {
            _isTrainingMode = false;
            return;
        }

        // 2. Define what "enough data" means (e.g., 5 shots per club)
        int requiredBaselineShots = 5;

        // 3. Check if ANY active club in your bag is still learning
        // (We only check clubs you actually use, ignoring ones with 0 shots entirely if you don't carry them)
        bool stillLearning = _myBag.Where(c => c.ShotCount > 0).Any(c => c.ShotCount < requiredBaselineShots);

        // If every active club has at least 5 shots, graduate out of training mode!
        if (!stillLearning && _myBag.Sum(c => c.ShotCount) > 0)
        {
            _isTrainingMode = false;
        }
        else
        {
            _isTrainingMode = true;
        }
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
            SuggestionLabel.Text = $"Caddie: ({longestClub.Name})";
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

                if (_myBag.Count == 0)
                {
                    _myBag = await _dbService.GetMyBagAsync();
                    
                }
                BindableLayout.SetItemsSource(ClubFlexLayout, _myBag);
                ClubSelectionOverlay.IsVisible = true;
            }

            // --- THE HYBRID AUTO-SCORER ---
            var currentHole = _holes[_currentIndex];

            // 1. If this was a real shot (>10 yds) AND we are outside the 20-yard Green Zone
            if (shotDistanceToLog >= 10 && _currentCenterYards > 20)
            {
                // Max score safety limit of 15
                if (_roundScores.ContainsKey(currentHole.HoleNumber) && _roundScores[currentHole.HoleNumber] < 15)
                {
                    _roundScores[currentHole.HoleNumber]++;

                    await EvaluateFIRAsync(currentHole);                    

                    // Safely update the UI thread
                    Dispatcher.Dispatch(() =>
                    {
                        ScoreLabel.Text = _roundScores[currentHole.HoleNumber].ToString();
                    });
                }
            }

            // 2. Format the Current Stroke for the voice engine
            int currentStroke = _roundScores.ContainsKey(currentHole.HoleNumber) ? _roundScores[currentHole.HoleNumber] : 1;
            string strokeSpeech = $"Stroke {currentStroke}. ";

            // --- COMPLIMENTS ---
            if (shotDistanceToLog >= 250)
            {
                string[] bigDrives = { "Wow, absolute nuke!", "Killer drive!", "Smashed it!", "Great shot!" };
                caddieCompliment = bigDrives[rnd.Next(bigDrives.Length)] + " ";
            }
            else if (shotDistanceToLog >= 150)
            {
                string[] solidShots = { "Nice strike!", "Solid shot!", "Looking good!" };
                caddieCompliment = solidShots[rnd.Next(solidShots.Length)] + " ";
            }

            // --- ASSEMBLE THE FINAL SPEECH ---
            string yardageSpeech = YardageLabel.Text.Replace("YDS", "yards to the center. ");
            string caddieSpeech = GetSpokenCaddieSuggestion();

            // "Nice strike! 145 yards to the center. Stroke 2. I suggest 8 iron."
            string finalSpeech = $"{caddieCompliment}{yardageSpeech}{strokeSpeech}{caddieSpeech}";

            if (_caddieVoice != null)
            {
                await TextToSpeech.Default.SpeakAsync(finalSpeech, _caddieVoice);
            }
            else
            {
                await TextToSpeech.Default.SpeakAsync(finalSpeech);
            }

            _lastAnnouncedLocation = _lastKnownLocation;
            _lastShotDistance = 0;
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
            _roundScores[currentHole.HoleNumber] = 1;
        }

        if (!_roundPutts.ContainsKey(currentHole.HoleNumber))
        {
            _roundPutts[currentHole.HoleNumber] = 0;
        }

        PuttLabel.Text = _roundPutts[currentHole.HoleNumber].ToString();
        ScoreLabel.Text = _roundScores[currentHole.HoleNumber].ToString();

        if (_lastKnownLocation != null)
        {
            UpdateYardages(_lastKnownLocation);
        }
        else
        {
            Dispatcher.Dispatch(() =>
            {
                BackYardageLabel.Text = "-- B";
                YardageLabel.Text = "-- YDS";
                FrontYardageLabel.Text = "-- F";
            });
        }

        // 1. Fetch the map features for this specific hole from SQLite
        var holeFeatures = await _dbService.GetMapFeaturesForHoleAsync(_courseName, currentHole.HoleNumber);

        // CACHE THE GREEN POLYGON FOR LIVE YARDAGES
        var greenFeature = holeFeatures.FirstOrDefault(f => f.FeatureType == "Green");
        if (greenFeature != null)
        {
            var rawNodes = System.Text.Json.JsonSerializer.Deserialize<List<MainPage.OverpassGeometry>>(greenFeature.SerializedCoordinates);
            _currentGreenPolygon = rawNodes?.Select(n => new Location(n.lat, n.lon)).ToList() ?? new List<Location>();
        }
        else
        {
            _currentGreenPolygon.Clear();
        }
       

        // --- NEW: TEE BOX LOGIC ---
        // 1. Set default fallback coordinates (the main hole line)
        double activeTeeLat = currentHole.TeeLatitude;
        double activeTeeLon = currentHole.TeeLongitude;

        if (!string.IsNullOrEmpty(_selectedTeeColor))
        {
            // 2. Search for the specific tee color the user selected
            var teeFeature = holeFeatures.FirstOrDefault(f => f.FeatureType == "Tee" && f.TeeColor?.ToLower() == _selectedTeeColor.ToLower());
            if (teeFeature != null && !string.IsNullOrEmpty(teeFeature.SerializedCoordinates))
            {
                var rawTeeNodes = System.Text.Json.JsonSerializer.Deserialize<List<MainPage.OverpassGeometry>>(teeFeature.SerializedCoordinates);
                if (rawTeeNodes != null && rawTeeNodes.Count > 0)
                {
                    // Grab the actual coordinates for this specific tee box
                    activeTeeLat = rawTeeNodes.First().lat;
                    activeTeeLon = rawTeeNodes.First().lon;
                }
            }
        }

        // 3. Calculate map framing using the ACTIVE tee coordinates
        double midLat = (activeTeeLat + currentHole.GreenLatitude) / 2;
        double midLon = (activeTeeLon + currentHole.GreenLongitude) / 2;
        double bearing = CalculateBearing(activeTeeLat, activeTeeLon, currentHole.GreenLatitude, currentHole.GreenLongitude);

        double distanceMeters = Location.CalculateDistance(activeTeeLat, activeTeeLon, currentHole.GreenLatitude, currentHole.GreenLongitude, DistanceUnits.Kilometers) * 1000.0;
        if (distanceMeters < 50) distanceMeters = 50;

        Dispatcher.Dispatch(() =>
        {
            // Clear old pins and old drawings
            CourseMap.Pins.Clear();
            CourseMap.MapElements.Clear();

            // 2. Draw the Polygons onto the MAUI Map
            foreach (var feature in holeFeatures)
            {
                var rawNodes = System.Text.Json.JsonSerializer.Deserialize<List<MainPage.OverpassGeometry>>(feature.SerializedCoordinates);
                if (rawNodes == null || rawNodes.Count == 0) continue;

                var polygon = new Microsoft.Maui.Controls.Maps.Polygon
                {
                    StrokeWidth = 2
                };

                // Assign colors based on what the feature is (using ARGB for transparency)
                switch (feature.FeatureType)
                {
                    case "Fairway":
                        polygon.FillColor = Color.FromRgba(144, 238, 144, 100); // Light Green, semi-transparent
                        polygon.StrokeColor = Colors.LightGreen;
                        break;
                    case "Green":
                        polygon.FillColor = Color.FromRgba(0, 100, 0, 150);     // Dark Green, semi-transparent
                        polygon.StrokeColor = Colors.DarkGreen;
                        break;
                    case "Bunker":
                        polygon.FillColor = Color.FromRgba(245, 245, 220, 150); // Sand / Beige
                        polygon.StrokeColor = Colors.SandyBrown;
                        break;
                    case "Water":
                        polygon.FillColor = Color.FromRgba(0, 0, 255, 120);     // Blue, semi-transparent
                        polygon.StrokeColor = Colors.LightBlue;
                        break;
                    default:
                        continue; // Skip unrecognized features
                }

                // Plot the points
                foreach (var node in rawNodes)
                {
                    polygon.Geopath.Add(new Location(node.lat, node.lon));
                }

                // Add the shape to the map layer
                CourseMap.MapElements.Add(polygon);
            }

#if ANDROID
            var mapHandler = CourseMap.Handler as Microsoft.Maui.Maps.Handlers.MapHandler;
            if (mapHandler?.Map is Android.Gms.Maps.GoogleMap googleMap)
            {
                // Do NOT call googleMap.Clear() here anymore! 
                // It will wipe out the MAUI MapElements we just drew.

                googleMap.UiSettings.MyLocationButtonEnabled = false;
                googleMap.UiSettings.ZoomControlsEnabled = false;
                googleMap.UiSettings.CompassEnabled = false;
                googleMap.UiSettings.MapToolbarEnabled = false;
            
                var teePoint = new Android.Gms.Maps.Model.LatLng(activeTeeLat, activeTeeLon);
                var greenPoint = new Android.Gms.Maps.Model.LatLng(currentHole.GreenLatitude, currentHole.GreenLongitude);

                var context = Android.App.Application.Context;

                float perfectZoom = CalculatePerfectZoom(midLat, distanceMeters, CourseMap.Height);

                var cinematicCamera = new Android.Gms.Maps.Model.CameraPosition.Builder()
                    .Target(new Android.Gms.Maps.Model.LatLng(midLat, midLon))
                    .Zoom(perfectZoom)
                    .Bearing((float)bearing)
                    .Build();

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
                MinimumTime = TimeSpan.FromSeconds(5) // Update every 2 seconds
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
    
    private void UpdateYardages(Location myLocation)
    {
        if (_holes.Count == 0 || myLocation == null) return;

        var currentHole = _holes[_currentIndex];

        // Center is still calculated from the explicit green center coordinates
        double distanceMeters = Location.CalculateDistance(
            myLocation.Latitude, myLocation.Longitude,
            currentHole.GreenLatitude, currentHole.GreenLongitude,
            DistanceUnits.Kilometers) * 1000.0;

        double centerYards = Math.Round(distanceMeters * 1.09361);
        _currentCenterYards = centerYards;

        double frontYards = 0;
        double backYards = 0;

        // USE TRUE POLYGON MATH IF WE HAVE IT
        if (_currentGreenPolygon != null && _currentGreenPolygon.Count > 0)
        {
            double minDistance = double.MaxValue;
            double maxDistance = double.MinValue;

            foreach (var node in _currentGreenPolygon)
            {
                double nodeDistMeters = Location.CalculateDistance(
                    myLocation.Latitude, myLocation.Longitude,
                    node.Latitude, node.Longitude,
                    DistanceUnits.Kilometers) * 1000.0;

                if (nodeDistMeters < minDistance) minDistance = nodeDistMeters;
                if (nodeDistMeters > maxDistance) maxDistance = nodeDistMeters;
            }

            frontYards = Math.Round(minDistance * 1.09361);
            backYards = Math.Round(maxDistance * 1.09361);

            // Safety check to ensure the UI stays logical if a weirdly shaped green acts up
            if (frontYards > centerYards) frontYards = centerYards - 1;
            if (backYards < centerYards) backYards = centerYards + 1;
        }
        else
        {
            // FALLBACK: Use 15-yard estimation if OSM data is missing a green
            backYards = centerYards + 15;
            frontYards = Math.Max(0, centerYards - 15);
        }

        Dispatcher.Dispatch(() =>
        {
            BackYardageLabel.Text = $"{backYards} B";
            YardageLabel.Text = $"{centerYards} YDS";
            FrontYardageLabel.Text = $"{frontYards} F";

            UpdateClubSuggestion(centerYards);
        });
    }
 

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
    

    private async void OnNextHole_Clicked(object sender, EventArgs e)
    {
        if (_holes.Count == 0) return;
        _currentIndex = (_currentIndex + 1) % _holes.Count;

        if (!_isPreviewMode)
        {
            _lastAnnouncedLocation = null;
            _announceTimer.Stop();
            _announceTimer.Start(); // NEW: Kickstart the tee-box announcement
        }

        await DisplayCurrentHoleAsync();
    }

    private async void OnPrevHole_Clicked(object sender, EventArgs e)
    {
        if (_holes.Count == 0) return;
        _currentIndex = (_currentIndex - 1 + _holes.Count) % _holes.Count;

        if (!_isPreviewMode)
        {
            _lastAnnouncedLocation = null;
            _announceTimer.Stop();
            _announceTimer.Start(); // NEW: Kickstart the tee-box announcement
        }

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


    private async void OnPlusScore_Clicked(object sender, EventArgs e)
    {
        if (_holes.Count == 0) return;

        var currentHole = _holes[_currentIndex];

        // Max score of 15 (just in case it's a rough day out there)
        if (_roundScores[currentHole.HoleNumber] < 15)
        {
            _roundScores[currentHole.HoleNumber]++;
            ScoreLabel.Text = _roundScores[currentHole.HoleNumber].ToString();
            await EvaluateFIRAsync(currentHole);
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
            MuteSlashPath.IsVisible = true; // Shows the red/white cross-out slash
            _announceTimer.Stop(); // Kill any pending speech
        }
        else
        {
            MuteSlashPath.IsVisible = false; // Hides the slash, showing active speaker
        }
    }

    private async void OnFinishRound_Clicked(object sender, EventArgs e)
    {
        // Ask for confirmation before ending the round
        bool confirm = await DisplayAlert("Finish Round", "Are you sure you want to save this scorecard and end the round?", "Yes, Save", "Keep Playing");

        if (confirm)
        {
            // Save to SQLite
            await _dbService.SaveCompletedRoundAsync(_courseName, _holes, _roundScores, _roundPutts,_roundFairways);

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

    private void OnPlusPutt_Clicked(object sender, EventArgs e)
    {
        if (_holes.Count == 0) return;
        var currentHole = _holes[_currentIndex];

        
        if (_roundPutts[currentHole.HoleNumber] < 10)
        {
            _roundPutts[currentHole.HoleNumber]++;
            PuttLabel.Text = _roundPutts[currentHole.HoleNumber].ToString();

            // LINKED LOGIC: A putt is a stroke! Increment the total score too.
            if (_roundScores[currentHole.HoleNumber] < 15)
            {
                _roundScores[currentHole.HoleNumber]++;
                ScoreLabel.Text = _roundScores[currentHole.HoleNumber].ToString();
            }
        }
    }

    private void OnMinusPutt_Clicked(object sender, EventArgs e)
    {
        if (_holes.Count == 0) return;
        var currentHole = _holes[_currentIndex];

        // Don't let putts go below 0
        if (_roundPutts[currentHole.HoleNumber] > 0)
        {
            _roundPutts[currentHole.HoleNumber]--;
            PuttLabel.Text = _roundPutts[currentHole.HoleNumber].ToString();

            // LINKED LOGIC: Removing a putt removes a total stroke.
            if (_roundScores[currentHole.HoleNumber] > 1)
            {
                _roundScores[currentHole.HoleNumber]--;
                ScoreLabel.Text = _roundScores[currentHole.HoleNumber].ToString();
            }
        }
    }


    public bool IsPointInPolygon(Microsoft.Maui.Devices.Sensors.Location point, List<Microsoft.Maui.Devices.Sensors.Location> polygon)
    {
        if (polygon == null || polygon.Count < 3) return false;

        bool isInside = false;
        int j = polygon.Count - 1;

        for (int i = 0; i < polygon.Count; i++)
        {
            // Check if the ray crosses the polygon segment
            if (((polygon[i].Latitude > point.Latitude) != (polygon[j].Latitude > point.Latitude)) &&
                (point.Longitude < (polygon[j].Longitude - polygon[i].Longitude) * (point.Latitude - polygon[i].Latitude) / (polygon[j].Latitude - polygon[i].Latitude) + polygon[i].Longitude))
            {
                isInside = !isInside;
            }
            j = i;
        }

        return isInside;
    }

    private async Task EvaluateFIRAsync(GolfHole currentHole)
    {
        if (_roundScores[currentHole.HoleNumber] == 2 && currentHole.Par > 3)
        {
            var holeFeatures = await _dbService.GetMapFeaturesForHoleAsync(_courseName, currentHole.HoleNumber);
            var fairwayFeature = holeFeatures.FirstOrDefault(f => f.FeatureType == "Fairway");

            if (fairwayFeature != null && _lastKnownLocation != null)
            {
                // 1. Deserialize using the exact matching model from MainPage
                var rawNodes = System.Text.Json.JsonSerializer.Deserialize<List<MainPage.OverpassGeometry>>(fairwayFeature.SerializedCoordinates);

                // 2. Convert them into MAUI Location objects for the math engine
                var fairwayNodes = rawNodes.Select(n => new Location(n.lat, n.lon)).ToList();

                // 3. Run the math
                bool hitFairway = IsPointInPolygon(_lastKnownLocation, fairwayNodes);
                _roundFairways[currentHole.HoleNumber] = hitFairway;
            }
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

           
        }

        // Save them to memory
        _myBag = bag;
    }
}