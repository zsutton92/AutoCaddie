using AutoCaddie.Models;
using AutoCaddie.Services;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.ApplicationModel;

namespace AutoCaddie;

public partial class CoursePage : ContentPage
{
    private CourseWeather _courseWeather;
    private DatabaseService _dbService = new();
    private IDispatcherTimer _weatherRefreshTimer;
    private Location _lastKnownLocation;
    private Location _lastAnnouncedLocation;
    private Location _lastElevationLocation = null;
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
    private bool _showLayupArcs = true;

    private bool _hasReachedGreen = false;

    private int _currentIndex = 0;

    private double _lastShotDistance = 0;
    private double _pendingShotDistance = 0;
    private double _currentCenterYards = 0;
    private double? _currentGreenElevationMeters = null;
    private double? _currentUserElevationMeters = null;
    
    private IDispatcherTimer _announceTimer;
    private SpeechOptions _caddieVoice;

    public CoursePage(string courseName, string teeColor = null, bool isPreviewMode = false, CourseWeather weather = null)
    {
        InitializeComponent();

        _courseName = courseName;
        _selectedTeeColor = teeColor;
        _isPreviewMode = isPreviewMode;
        _courseWeather = weather ?? new CourseWeather();

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

                _weatherRefreshTimer = Dispatcher.CreateTimer();
                _weatherRefreshTimer.Interval = TimeSpan.FromMinutes(30);
                _weatherRefreshTimer.Tick += async (s, e) => await RefreshWeatherInBackgroundAsync();
                _weatherRefreshTimer.Start();

                EvaluateTrainingMode();
                await InitializeCaddieVoice();
                await StartGpsTracking();
            }
        };
    }

    private void UpdateWindOverlayForCurrentHole(double holeBearing)
    {
        Dispatcher.Dispatch(() =>
        {
            WindSpeedLabel.Text = $"{Math.Round(_courseWeather.WindSpeedMph)} mph";

            if (_courseWeather.Condition == "Rain") WeatherIconLabel.Text = "🌧️";
            else if (_courseWeather.Condition == "Cloud") WeatherIconLabel.Text = "☁️";
            else if (_courseWeather.Condition == "Snow") WeatherIconLabel.Text = "❄️";
            else if (_courseWeather.Condition == "Storm") WeatherIconLabel.Text = "⛈️";
            else WeatherIconLabel.Text = "☀️";

            double blowingTo = (_courseWeather.WindDirectionDegrees + 180) % 360;
            double relativeAngle = (blowingTo - holeBearing + 360) % 360;

            WindDirectionArrow.Rotation = relativeAngle;
        });
    }

    private async Task RefreshWeatherInBackgroundAsync()
    {
        if (_holes.Count == 0) return;

        try
        {
            double lat = _holes.First().TeeLatitude;
            double lon = _holes.First().TeeLongitude;

            using HttpClient client = new HttpClient();
            string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&temperature_unit=fahrenheit&wind_speed_unit=mph";

            var response = await client.GetStringAsync(url);
            var weatherData = System.Text.Json.JsonSerializer.Deserialize<OpenMeteoMapResponse>(response, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (weatherData?.current != null)
            {
                _courseWeather.WindSpeedMph = weatherData.current.wind_speed_10m;
                _courseWeather.WindDirectionDegrees = weatherData.current.wind_direction_10m;
                _courseWeather.Temperature = weatherData.current.temperature_2m;
                _courseWeather.LastUpdated = DateTime.Now;

                // Force the UI to redraw immediately with the new wind direction
                var currentHole = _holes[_currentIndex];
                double bearing = CalculateBearing(currentHole.TeeLatitude, currentHole.TeeLongitude, currentHole.GreenLatitude, currentHole.GreenLongitude);
                UpdateWindOverlayForCurrentHole(bearing);
            }
        }
        catch { /* Fail silently, keep using the old weather data */ }
    }

    private void EvaluateTrainingMode()
    {
        bool isManuallyDisabled = Preferences.Default.Get("DisableTrainingMode", false);
        if (isManuallyDisabled)
        {
            _isTrainingMode = false;
            return;
        }

        int requiredBaselineShots = 5;
        bool stillLearning = _myBag.Where(c => c.ShotCount > 0).Any(c => c.ShotCount < requiredBaselineShots);

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

        var activeClubs = _myBag.Where(c => c.AverageYardage > 0).ToList();

        if (activeClubs.Count == 0)
        {
            SuggestionLabel.Text = "Caddie: Hit shots to learn!";
            return;
        }

        if (_lastKnownLocation != null && _currentGreenPolygon != null && _currentGreenPolygon.Count > 0)
        {
            if (IsPointInPolygon(_lastKnownLocation, _currentGreenPolygon))
            {
                SuggestionLabel.Text = "Caddie: Putter";
                return;
            }
        }

        // --- NEW: Raw Yardage for Short Game (Ignore Wind) ---
        // We use _currentCenterYards here because a 15-yard putt shouldn't 
        // switch to a chip just because there is a 10mph headwind!
        if (_currentCenterYards <= 20)
        {
            SuggestionLabel.Text = "Caddie: Putter";
            return;
        }

        if (_currentCenterYards <= 40)
        {
            SuggestionLabel.Text = "Caddie: Pitch / Chip";
            return;
        }

        var longestClub = activeClubs.OrderByDescending(c => c.AverageYardage).First();

        if (targetYards >= longestClub.AverageYardage + 15)
        {
            SuggestionLabel.Text = $"Caddie: ({longestClub.Name})";
            return;
        }

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

            var currentHole = _holes[_currentIndex];

            double distToCenterFromLastShot = _lastAnnouncedLocation != null ?
                Location.CalculateDistance(_lastAnnouncedLocation.Latitude, _lastAnnouncedLocation.Longitude, currentHole.GreenLatitude, currentHole.GreenLongitude, DistanceUnits.Kilometers) * 1093.61 : 999;

            // --- NEW: Approach Shot Logic ---
            // A shot counts if it's > 15 yards AND (it started > 30 yards from the pin OR ended > 30 yards from the pin)
            // This prevents 15-yard walks back-and-forth across the green from adding ghost strokes
           
            if (shotDistanceToLog >= 15 && (distToCenterFromLastShot > 30 || _currentCenterYards > 30))
            {
                if (_roundScores.ContainsKey(currentHole.HoleNumber) && _roundScores[currentHole.HoleNumber] < 15)
                {
                    _roundScores[currentHole.HoleNumber]++;

                    await EvaluateFIRAsync(currentHole);

                    Dispatcher.Dispatch(() =>
                    {
                        ScoreLabel.Text = _roundScores[currentHole.HoleNumber].ToString();
                    });

                    UpdateRoundScoreToPar();
                }
            }

            int currentStroke = _roundScores.ContainsKey(currentHole.HoleNumber) ? _roundScores[currentHole.HoleNumber] : 1;
            string strokeSpeech = $"Stroke {currentStroke}. ";

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

            //string yardageSpeech = YardageLabel.Text.Replace("YDS", "yards to the center. ");
            string yardageSpeech = $"{_currentCenterYards} yards to the center. Playing like { PlaysLikeLabel.Text.Replace("Plays ", "").Replace("YDS","yards")}. ";

            // --- CHECK FOR HAZARDS NEARBY ---
            string hazardSpeech = "";
            if (_lastKnownLocation != null)
            {
                hazardSpeech = await CheckNearestHazardsAsync(_lastKnownLocation) ?? "";
            }

            string caddieSpeech = GetSpokenCaddieSuggestion();

            // string finalSpeech = $"{caddieCompliment}{yardageSpeech}{strokeSpeech}{caddieSpeech}";
            // Example: "Great shot! Stroke 2. 145 yards. Playing like 152.  I suggest 8 iron."
            string finalSpeech = $"{strokeSpeech}{caddieCompliment}{hazardSpeech}{yardageSpeech}{caddieSpeech}";

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

    private float CalculatePerfectZoom(double midLat, double distanceMeters, double mapHeightDp)
    {
        double paddingDp = 50;
        double availableDp = mapHeightDp - paddingDp;
        if (availableDp <= 0) availableDp = 500;

        double bufferedDistance = distanceMeters * 1.25;
        double equatorLength = 40075016.686;
        double metersPerDpAtZoom0 = equatorLength / 256.0;
        double latRadians = midLat * (Math.PI / 180.0);

        double requiredMetersPerDp = bufferedDistance / availableDp;
        double zoom = Math.Log2((metersPerDpAtZoom0 * Math.Cos(latRadians)) / requiredMetersPerDp);

        if (zoom > 20f) zoom = 20f;
        if (zoom < 14f) zoom = 14f;

        return (float)zoom;
    }


    private async Task DisplayCurrentHoleAsync()
    {
        if (_holes.Count == 0) return;

        var currentHole = _holes[_currentIndex];

        _currentGreenElevationMeters = null;
        _currentUserElevationMeters = null;
        _lastElevationLocation = null;

        Task.Run(async () =>
        {
            _currentGreenElevationMeters = await FetchElevationAsync(currentHole.GreenLatitude, currentHole.GreenLongitude);
        });

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
                PlaysLikeLabel.Text = "-- YDS";
                BackYardageLabel.Text = "-- B";
                YardageLabel.Text = "-- C";
                FrontYardageLabel.Text = "-- F";
            });
        }

        var holeFeatures = await _dbService.GetMapFeaturesForHoleAsync(_courseName, currentHole.HoleNumber);

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

        double activeTeeLat = currentHole.TeeLatitude;
        double activeTeeLon = currentHole.TeeLongitude;

        if (!string.IsNullOrEmpty(_selectedTeeColor))
        {
            var teeFeature = holeFeatures.FirstOrDefault(f => f.FeatureType == "Tee" && f.TeeColor?.ToLower() == _selectedTeeColor.ToLower());
            if (teeFeature != null && !string.IsNullOrEmpty(teeFeature.SerializedCoordinates))
            {
                var rawTeeNodes = System.Text.Json.JsonSerializer.Deserialize<List<MainPage.OverpassGeometry>>(teeFeature.SerializedCoordinates);
                if (rawTeeNodes != null && rawTeeNodes.Count > 0)
                {
                    activeTeeLat = rawTeeNodes.First().lat;
                    activeTeeLon = rawTeeNodes.First().lon;
                }
            }
        }

        double midLat = (activeTeeLat + currentHole.GreenLatitude) / 2;
        double midLon = (activeTeeLon + currentHole.GreenLongitude) / 2;
        double bearing = CalculateBearing(activeTeeLat, activeTeeLon, currentHole.GreenLatitude, currentHole.GreenLongitude);

        UpdateWindOverlayForCurrentHole(bearing);

        double distanceMeters = Location.CalculateDistance(activeTeeLat, activeTeeLon, currentHole.GreenLatitude, currentHole.GreenLongitude, DistanceUnits.Kilometers) * 1000.0;
        if (distanceMeters < 50) distanceMeters = 50;

        Dispatcher.Dispatch(() =>
        {
            CourseMap.Pins.Clear();
            CourseMap.MapElements.Clear();

#if ANDROID
            var mapHandlerForClearing = CourseMap.Handler as Microsoft.Maui.Maps.Handlers.MapHandler;
            if (mapHandlerForClearing?.Map is Android.Gms.Maps.GoogleMap googleMapForClearing)
            {
                googleMapForClearing.Clear();
            }
#endif

            // --- NEW: DRAW DYNAMIC LAYUP ARCS & AXIS ---
            if (_showLayupArcs)
            {
                var greenCenter = new Location(currentHole.GreenLatitude, currentHole.GreenLongitude);
                var activeTee = new Location(activeTeeLat, activeTeeLon);

                // 1. Draw the Center Axis Line (from the Green out to 220 yards)
                double axisBearing = CalculateBearing(greenCenter.Latitude, greenCenter.Longitude, activeTee.Latitude, activeTee.Longitude);
                double axisDistMeters = 220 * 0.9144;
                double earthRad = 6378137.0;
                double angDist = axisDistMeters / earthRad;

                double cLatRad = greenCenter.Latitude * (Math.PI / 180.0);
                double cLonRad = greenCenter.Longitude * (Math.PI / 180.0);
                double bRad = axisBearing * (Math.PI / 180.0);

                double endLatRad = Math.Asin(Math.Sin(cLatRad) * Math.Cos(angDist) + Math.Cos(cLatRad) * Math.Sin(angDist) * Math.Cos(bRad));
                double endLonRad = cLonRad + Math.Atan2(Math.Sin(bRad) * Math.Sin(angDist) * Math.Cos(cLatRad), Math.Cos(angDist) - Math.Sin(cLatRad) * Math.Sin(endLatRad));

                var axisLine = new Microsoft.Maui.Controls.Maps.Polyline
                {
                    StrokeColor = Color.FromRgba(200, 200, 200, 150), // Subtle Gray
                    StrokeWidth = 2
                };
                axisLine.Geopath.Add(greenCenter);
                axisLine.Geopath.Add(new Location(endLatRad * (180.0 / Math.PI), endLonRad * (180.0 / Math.PI)));

                CourseMap.MapElements.Add(axisLine);

                // 2. Draw the 200-Yard Arc (Blue)
                CourseMap.MapElements.Add(CreateLayupArc(greenCenter, activeTee, 200, Color.FromRgba(65, 105, 225, 200)));

                // 3. Draw the 150-Yard Arc (White)
                CourseMap.MapElements.Add(CreateLayupArc(greenCenter, activeTee, 150, Color.FromRgba(255, 255, 255, 200)));

                // 4. Draw the 100-Yard Arc (Red)
                CourseMap.MapElements.Add(CreateLayupArc(greenCenter, activeTee, 100, Color.FromRgba(220, 20, 60, 200)));
            }

            foreach (var feature in holeFeatures)
            {
                var rawNodes = System.Text.Json.JsonSerializer.Deserialize<List<MainPage.OverpassGeometry>>(feature.SerializedCoordinates);
                if (rawNodes == null || rawNodes.Count == 0) continue;

                var polygon = new Microsoft.Maui.Controls.Maps.Polygon
                {
                    StrokeWidth = 2
                };

                switch (feature.FeatureType)
                {
                    case "Fairway":
                        polygon.FillColor = Color.FromRgba(144, 238, 144, 100);
                        polygon.StrokeColor = Colors.LightGreen;
                        break;
                    case "Green":
                        polygon.FillColor = Color.FromRgba(0, 100, 0, 150);
                        polygon.StrokeColor = Colors.DarkGreen;
                        break;
                    case "Bunker":
                        polygon.FillColor = Color.FromRgba(245, 245, 220, 150);
                        polygon.StrokeColor = Colors.SandyBrown;
                        break;
                    case "Water":
                        polygon.FillColor = Color.FromRgba(0, 0, 255, 120);
                        polygon.StrokeColor = Colors.LightBlue;
                        break;
                    default:
                        continue;
                }

                //foreach (var node in rawNodes)
                //{
                //    polygon.Geopath.Add(new Location(node.lat, node.lon));
                //}
                var rawLocations = rawNodes.Select(n => new Location(n.lat, n.lon)).ToList();
                var smoothedLocations = SmoothPolygon(rawLocations, 2);

                foreach (var loc in smoothedLocations)
                {
                    polygon.Geopath.Add(loc);
                }

                CourseMap.MapElements.Add(polygon);
            }

#if ANDROID
            var mapHandler = CourseMap.Handler as Microsoft.Maui.Maps.Handlers.MapHandler;
            if (mapHandler?.Map is Android.Gms.Maps.GoogleMap googleMap)
            {
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

        UpdateRoundScoreToPar();
    }

    // Calculates points along a curve to create a perfect layup arc
    private Microsoft.Maui.Controls.Maps.Polyline CreateLayupArc(Location greenCenter, Location teeBox, double distanceYards, Color arcColor)
    {
        var polyline = new Microsoft.Maui.Controls.Maps.Polyline
        {
            StrokeColor = arcColor,
            StrokeWidth = 3
        };

        // 1. Get the direction from the Green pointing BACK towards the Tee
        double baseBearing = CalculateBearing(greenCenter.Latitude, greenCenter.Longitude, teeBox.Latitude, teeBox.Longitude);

        double distanceMeters = distanceYards * 0.9144;
        double earthRadius = 6378137.0; // Earth's radius in meters
        double angularDistance = distanceMeters / earthRadius;

        double centerLatRad = greenCenter.Latitude * (Math.PI / 180.0);
        double centerLonRad = greenCenter.Longitude * (Math.PI / 180.0);

        // 2. Sweep a curve from -45 degrees to +45 degrees across the fairway
        for (int i = -45; i <= 45; i += 5)
        {
            double currentBearing = (baseBearing + i + 360) % 360;
            double bearingRad = currentBearing * (Math.PI / 180.0);

            // Spherical trigonometry to find the exact Lat/Lon of each point on the curve
            double pointLatRad = Math.Asin(Math.Sin(centerLatRad) * Math.Cos(angularDistance) +
                                           Math.Cos(centerLatRad) * Math.Sin(angularDistance) * Math.Cos(bearingRad));

            double pointLonRad = centerLonRad + Math.Atan2(Math.Sin(bearingRad) * Math.Sin(angularDistance) * Math.Cos(centerLatRad),
                                                           Math.Cos(angularDistance) - Math.Sin(centerLatRad) * Math.Sin(pointLatRad));

            polyline.Geopath.Add(new Location(pointLatRad * (180.0 / Math.PI), pointLonRad * (180.0 / Math.PI)));
        }

        return polyline;
    }

    private async Task StartGpsTracking()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.LocationAlways>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.LocationAlways>();
        }

        if (status == PermissionStatus.Granted || await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>() == PermissionStatus.Granted)
        {
            CourseMap.IsShowingUser = true;

            Geolocation.LocationChanged += Geolocation_LocationChanged;
            var request = new GeolocationListeningRequest
            {
                DesiredAccuracy = GeolocationAccuracy.Best,
                MinimumTime = TimeSpan.FromSeconds(5)
            };

            await Geolocation.StartListeningForegroundAsync(request);
            _isTracking = true;

            _announceTimer.Start();

#if ANDROID
            var intent = new Android.Content.Intent(Android.App.Application.Context, typeof(AutoCaddie.Platforms.Android.GpsForegroundService));
            Android.App.Application.Context.StartForegroundService(intent);
#endif
        }
    }

    private string GetSpokenCaddieSuggestion()
    {
        string suggestionText = SuggestionLabel.Text;

        if (suggestionText.Contains("Calculating") || suggestionText.Contains("Hit shots"))
        {
            return "";
        }

        string club = suggestionText.Replace("Caddie: ", "");

        club = club.Replace("Dr", "Driver")
                   .Replace("Ptt", "Putter")
                   .Replace("PW", "Pitching Wedge")
                   .Replace("GW", "Gap Wedge")
                   .Replace("SW", "Sand Wedge")
                   .Replace("LW", "Lob Wedge");

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

        double distanceMeters = Location.CalculateDistance(
            myLocation.Latitude, myLocation.Longitude,
            currentHole.GreenLatitude, currentHole.GreenLongitude,
            DistanceUnits.Kilometers) * 1000.0;

        double centerYards = Math.Round(distanceMeters * 1.09361);
        _currentCenterYards = centerYards;

        double frontYards = 0;
        double backYards = 0;

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

            if (frontYards > centerYards) frontYards = centerYards - 1;
            if (backYards < centerYards) backYards = centerYards + 1;
        }
        else
        {
            backYards = centerYards + 15;
            frontYards = Math.Max(0, centerYards - 15);
        }

        // --- NEW: PLAYS LIKE MATH (WIND) ---

        // 1. Calculate the bearing from the USER to the GREEN 
        // (This ensures the wind math is accurate even if you hit your drive two fairways over)
        double userToGreenBearing = CalculateBearing(myLocation.Latitude, myLocation.Longitude, currentHole.GreenLatitude, currentHole.GreenLongitude);

        // 2. Determine where the wind is blowing TO
        double windBlowingTo = (_courseWeather.WindDirectionDegrees + 180) % 360;

        // 3. Calculate the relative angle of the wind to your specific shot line
        double relativeWindAngle = (windBlowingTo - userToGreenBearing + 360) % 360;

        // 4A. Calculate effective wind using Trigonometry (Cos(angle) * speed).
        // Tailwind (0 deg) = Cos(0) = 1. Headwind (180 deg) = Cos(180) = -1. Crosswind (90 deg) = Cos(90) = 0.
        double effectiveWindMph = Math.Cos(relativeWindAngle * (Math.PI / 180.0)) * _courseWeather.WindSpeedMph;

        // 4B. SLOPE MATH
        double slopeImpactYards = 0;
        if (_currentGreenElevationMeters.HasValue && _currentUserElevationMeters.HasValue)
        {
            // 1 meter of elevation change = ~1.09 yards of distance change
            // If the Green is higher (+), it adds yards. If lower (-), it subtracts.
            slopeImpactYards = (_currentGreenElevationMeters.Value - _currentUserElevationMeters.Value) * 1.09361;
        }

        // 5. Golf Rule of Thumb: 1 mph of direct wind = ~1 yard of impact.
        // Since effectiveWindMph is positive for a tailwind, we SUBTRACT it to get the playing distance.
        double playsLikeYards = Math.Round(centerYards - effectiveWindMph + slopeImpactYards);

        Dispatcher.Dispatch(() =>
        {
            // Update the huge Plays Like label
            PlaysLikeLabel.Text = $"{playsLikeYards} YDS";

            // Update the small labels B, C, F
            BackYardageLabel.Text = $"{backYards} B";
            YardageLabel.Text = $"{centerYards} C";
            FrontYardageLabel.Text = $"{frontYards} F";

            //UpdateClubSuggestion(centerYards);
            UpdateClubSuggestion(playsLikeYards);
        });
    }

    private void Geolocation_LocationChanged(object sender, GeolocationLocationChangedEventArgs e)
    {
        var myLocation = e.Location;

        double distanceSinceLastPing = _lastKnownLocation != null ?
            Location.CalculateDistance(_lastKnownLocation, myLocation, DistanceUnits.Kilometers) * 1093.61 : 0;

        _lastKnownLocation = myLocation;
        UpdateYardages(_lastKnownLocation);

        var currentHole = _holes[_currentIndex];

        // 1. Check if they have reached the current green (within 50 yards of the pin)
        if (!_hasReachedGreen && _currentCenterYards <= 50)
        {
            _hasReachedGreen = true;
        }

        // 2. If they've visited the green, start looking for the next tee box
        if (_hasReachedGreen && _currentIndex < _holes.Count - 1)
        {
            var nextHole = _holes[_currentIndex + 1];

            double distToNextTeeMeters = Location.CalculateDistance(
                myLocation.Latitude, myLocation.Longitude,
                nextHole.TeeLatitude, nextHole.TeeLongitude,
                DistanceUnits.Kilometers) * 1000.0;

            double distToNextTeeYards = distToNextTeeMeters * 1.09361;

            // NEW: To transition, they must walk OFF the green (> 30 yards away) 
            // AND be within a massive 120-yard net of the next tee's center coordinates.
            if (distToNextTeeYards <= 120 && _currentCenterYards > 30)
            {
                // IMMEDIATELY disable the handshake so the GPS doesn't spam this
                _hasReachedGreen = false;

                // Force the UI update to run on the main thread
                Dispatcher.Dispatch(() =>
                {
                    OnNextHole_Clicked(this, EventArgs.Empty);
                });
            }
        }

        double distFromLastElevation = _lastElevationLocation != null ?
            Location.CalculateDistance(_lastElevationLocation, myLocation, DistanceUnits.Kilometers) * 1093.61 : 999;

        // Only ping the network if they moved 15+ yards to save battery
        if (distFromLastElevation > 15)
        {
            _lastElevationLocation = myLocation;
            Task.Run(async () =>
            {
                _currentUserElevationMeters = await FetchElevationAsync(myLocation.Latitude, myLocation.Longitude);

                // Force a quick yardage refresh to apply the new slope math!
                UpdateYardages(myLocation);
            });
        }

        bool isNewHole = _lastAnnouncedLocation == null;

        double yardsFromLastAnnounce = isNewHole ? 999 :
            Location.CalculateDistance(_lastAnnouncedLocation, myLocation, DistanceUnits.Kilometers) * 1093.61;

        if (yardsFromLastAnnounce >= 10)
        {
            if (distanceSinceLastPing > 1.5)
            {
                if (!isNewHole)
                {
                    _lastShotDistance = yardsFromLastAnnounce;
                }

                _announceTimer.Stop();
                _announceTimer.Start();
            }
        }
    }
    private void UpdateRoundScoreToPar()
    {
        if (_holes.Count == 0) return;

        var currentHole = _holes[_currentIndex];

        int displayStrokes = 0;
        int scoreDifference = 0;

        foreach (var kvp in _roundScores)
        {
            int holeNum = kvp.Key;
            int strokes = kvp.Value;
            int putts = _roundPutts.ContainsKey(holeNum) ? _roundPutts[holeNum] : 0;

            var hole = _holes.FirstOrDefault(h => h.HoleNumber == holeNum);
            if (hole != null)
            {
                // If it's 1 stroke and 0 putts, you haven't actually hit a ball on this hole yet.
                // We completely ignore it so it doesn't artificially inflate your tally.
                if (strokes == 1 && putts == 0)
                {
                    continue;
                }

                // A hole is "Completed" if we've moved past it, or if we've started putting on it.
                bool isCompleted = (holeNum < currentHole.HoleNumber) || putts > 0;

                if (isCompleted)
                {
                    displayStrokes += strokes;
                    scoreDifference += (strokes - hole.Par);
                }
                else
                {
                    // If you're currently playing the fairway, 'strokes' reflects the shot you are ABOUT to hit.
                    // So the number of strokes you've actually taken so far is (strokes - 1).
                    displayStrokes += (strokes - 1);
                }
            }
        }

        string formattedScore = scoreDifference == 0 ? "E" : (scoreDifference > 0 ? $"+{scoreDifference}" : $"{scoreDifference}");

        Dispatcher.Dispatch(() =>
        {
            RoundToParLabel.Text = $"{displayStrokes} ({formattedScore})";
        });
    }
    // Smooths jagged OpenStreetMap polygons into organic, natural golf course shapes
    private List<Location> SmoothPolygon(List<Location> points, int iterations = 2)
    {
        if (points == null || points.Count < 3) return points;

        var smoothed = new List<Location>(points);

        for (int i = 0; i < iterations; i++)
        {
            var temp = new List<Location>();
            for (int j = 0; j < smoothed.Count; j++)
            {
                var p1 = smoothed[j];
                var p2 = smoothed[(j + 1) % smoothed.Count]; // Wraps around to close the loop

                // Chaikin's magic numbers: grab points at 25% and 75% along the segment
                double qLat = 0.75 * p1.Latitude + 0.25 * p2.Latitude;
                double qLon = 0.75 * p1.Longitude + 0.25 * p2.Longitude;

                double rLat = 0.25 * p1.Latitude + 0.75 * p2.Latitude;
                double rLon = 0.25 * p1.Longitude + 0.75 * p2.Longitude;

                temp.Add(new Location(qLat, qLon));
                temp.Add(new Location(rLat, rLon));
            }
            smoothed = temp;
        }

        return smoothed;
    }

    private async void OnNextHole_Clicked(object sender, EventArgs e)
    {
        if (_holes.Count == 0) return;
        _currentIndex = (_currentIndex + 1) % _holes.Count;
        _hasReachedGreen = false;

        _lastShotDistance = 0;

        if (!_isPreviewMode)
        {
            _lastAnnouncedLocation = null;
            _announceTimer.Stop();
            _announceTimer.Start();
        }

        await DisplayCurrentHoleAsync();
    }

    private async void OnPrevHole_Clicked(object sender, EventArgs e)
    {
        if (_holes.Count == 0) return;
        _currentIndex = (_currentIndex - 1 + _holes.Count) % _holes.Count;
        _hasReachedGreen = false;
        _lastShotDistance = 0;

        if (!_isPreviewMode)
        {
            _lastAnnouncedLocation = null;
            _announceTimer.Stop();
            _announceTimer.Start();
        }

        await DisplayCurrentHoleAsync();
    }

    private async void OnYardageBadge_Tapped(object sender, TappedEventArgs e)
    {
        if (YardageLabel.Text != "-- C")
        {
            string speechText = YardageLabel.Text.Replace("C", "yards to the center");
            speechText += GetSpokenCaddieSuggestion();

            await TextToSpeech.Default.SpeakAsync(speechText, _caddieVoice);
        }
    }

    private async void OnPlusScore_Clicked(object sender, EventArgs e)
    {
        if (_holes.Count == 0) return;

        var currentHole = _holes[_currentIndex];

        if (_roundScores[currentHole.HoleNumber] < 15)
        {
            _roundScores[currentHole.HoleNumber]++;
            ScoreLabel.Text = _roundScores[currentHole.HoleNumber].ToString();
            UpdateRoundScoreToPar();
            await EvaluateFIRAsync(currentHole);
        }
    }

    private void OnMinusScore_Clicked(object sender, EventArgs e)
    {
        if (_holes.Count == 0) return;

        var currentHole = _holes[_currentIndex];

        if (_roundScores[currentHole.HoleNumber] > 1)
        {
            _roundScores[currentHole.HoleNumber]--;
            UpdateRoundScoreToPar();
            ScoreLabel.Text = _roundScores[currentHole.HoleNumber].ToString();
        }
    }

    private void OnMuteButton_Clicked(object sender, EventArgs e)
    {
        _isMuted = !_isMuted;

        if (_isMuted)
        {
            MuteSlashPath.IsVisible = true;
            _announceTimer.Stop();
        }
        else
        {
            MuteSlashPath.IsVisible = false;
        }
    }

    private async void OnFinishRound_Clicked(object sender, EventArgs e)
    {
        bool confirm = await DisplayAlert("Finish Round", "Are you sure you want to save this scorecard and end the round?", "Yes, Save", "Keep Playing");

        if (confirm)
        {
            await _dbService.SaveCompletedRoundAsync(_courseName, _holes, _roundScores, _roundPutts, _roundFairways);

            if (_isTracking)
            {
                Geolocation.LocationChanged -= Geolocation_LocationChanged;
                Geolocation.StopListeningForeground();
                _isTracking = false;
#if ANDROID
                var intent = new Android.Content.Intent(Android.App.Application.Context, typeof(AutoCaddie.Platforms.Android.GpsForegroundService));
                intent.SetAction("STOP_SERVICE");
                Android.App.Application.Context.StartService(intent);
#endif
            }
            if (_announceTimer != null)
            {
                _announceTimer.Stop();
            }

            await Navigation.PopAsync();
        }
    }

    private void OnShowScorecard_Tapped(object sender, EventArgs e)
    {
        BuildAndDisplayScorecard();
        ScorecardOverlay.IsVisible = true;
    }

    private void OnCloseScorecard_Clicked(object sender, EventArgs e)
    {
        ScorecardOverlay.IsVisible = false;
    }

    private void BuildAndDisplayScorecard()
    {
        ScorecardCourseNameLabel.Text = _courseName;

        // Reset grids
        Front9Grid.Children.Clear();
        Front9Grid.RowDefinitions.Clear();
        Front9Grid.ColumnDefinitions.Clear();

        Back9Grid.Children.Clear();
        Back9Grid.RowDefinitions.Clear();
        Back9Grid.ColumnDefinitions.Clear();

        var frontHoles = _holes.Where(h => h.HoleNumber <= 9).OrderBy(h => h.HoleNumber).ToList();
        var backHoles = _holes.Where(h => h.HoleNumber > 9 && h.HoleNumber <= 18).OrderBy(h => h.HoleNumber).ToList();

        PopulateScorecardHalf(Front9Grid, frontHoles, "OUT");
        PopulateScorecardHalf(Back9Grid, backHoles, "IN");

        // Calculate Overall Totals
        int totalPar = _holes.Sum(h => h.Par);
        int totalStrokes = 0;
        int completedPar = 0;

        foreach (var hole in _holes)
        {
            if (_roundScores.TryGetValue(hole.HoleNumber, out int strokes))
            {
                int putts = _roundPutts.TryGetValue(hole.HoleNumber, out int p) ? p : 0;
                bool isCompleted = (hole.HoleNumber <= _currentIndex) || putts > 0 || strokes > 1;

                if (isCompleted)
                {
                    totalStrokes += strokes;
                    completedPar += hole.Par;
                }
            }
        }

        ScorecardTotalParLabel.Text = totalPar.ToString();
        ScorecardTotalScoreLabel.Text = totalStrokes > 0 ? totalStrokes.ToString() : "--";

        int diff = totalStrokes - completedPar;
        if (totalStrokes == 0 || completedPar == 0)
        {
            ScorecardTotalToParLabel.Text = "E";
            ScorecardTotalToParLabel.TextColor = Colors.White;
        }
        else
        {
            ScorecardTotalToParLabel.Text = diff == 0 ? "E" : (diff > 0 ? $"+{diff}" : $"{diff}");
            ScorecardTotalToParLabel.TextColor = diff <= 0 ? Color.FromArgb("#69F0AE") : Color.FromArgb("#FF8A80");
        }
    }

    private void PopulateScorecardHalf(Grid targetGrid, List<GolfHole> holes, string totalLabel)
    {
        if (holes == null || holes.Count == 0) return;

        // Columns: Hole 1..9 (or 10..18) + OUT/IN summary column
        for (int i = 0; i <= holes.Count; i++)
        {
            targetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        }

        // Rows: Header (Hole #), Par, Score, Putts
        targetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Hole
        targetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Par
        targetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Score
        targetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Putts

        int totalHalfPar = 0;
        int totalHalfScore = 0;
        int totalHalfPutts = 0;

        for (int col = 0; col < holes.Count; col++)
        {
            var hole = holes[col];
            totalHalfPar += hole.Par;

            bool hasScore = _roundScores.TryGetValue(hole.HoleNumber, out int strokes);
            bool hasPutts = _roundPutts.TryGetValue(hole.HoleNumber, out int putts);

            // 1. Hole Header Label
            var holeLabel = new Label
            {
                Text = hole.HoleNumber.ToString(),
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#B39DDB"),
                HorizontalTextAlignment = TextAlignment.Center
            };
            targetGrid.Add(holeLabel, col, 0);

            // 2. Par Label
            var parLabel = new Label
            {
                Text = hole.Par.ToString(),
                FontSize = 12,
                TextColor = Color.FromArgb("#AAAAAA"),
                HorizontalTextAlignment = TextAlignment.Center
            };
            targetGrid.Add(parLabel, col, 1);

            // 3. Score Element (Traditional Circles and Squares)
            View scoreElement;

            if (hasScore && (strokes > 1 || putts > 0 || hole.HoleNumber < _currentIndex + 1))
            {
                totalHalfScore += strokes;
                int diff = strokes - hole.Par;

                var cellGrid = new Grid
                {
                    WidthRequest = 28,
                    HeightRequest = 28,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center
                };

                // Base text color
                Color scoreTextColor = Colors.White;
                Color shapeColor = Colors.White;

                // Color-code the shapes (Greenish for Under Par, Reddish for Over Par)
                if (diff < 0) shapeColor = Color.FromArgb("#69F0AE");
                else if (diff > 0) shapeColor = Color.FromArgb("#FF8A80");

                double thickness = 1.5;

                // Under Par (Circles)
                if (diff < 0)
                {
                    int circles = Math.Abs(diff);
                    if (circles > 3) circles = 3; // Cap at 3 (Albatross)

                    for (int i = 0; i < circles; i++)
                    {
                        cellGrid.Children.Add(new Microsoft.Maui.Controls.Shapes.Ellipse
                        {
                            Stroke = shapeColor,
                            StrokeThickness = thickness,
                            Margin = new Thickness(i * 3), // Nests the rings inward
                            Fill = Colors.Transparent
                        });
                    }
                }
                // Over Par (Squares)
                else if (diff > 0)
                {
                    int squares = diff;
                    if (squares > 3) squares = 3; // Cap at 3 (Triple Bogey)

                    for (int i = 0; i < squares; i++)
                    {
                        cellGrid.Children.Add(new Microsoft.Maui.Controls.Shapes.Rectangle
                        {
                            Stroke = shapeColor,
                            StrokeThickness = thickness,
                            Margin = new Thickness(i * 3), // Nests the squares inward
                            Fill = Colors.Transparent
                        });
                    }
                }

                // If Par (diff == 0), it adds no shapes, just the plain number.

                // Add the actual score number on top of the shapes
                cellGrid.Children.Add(new Label
                {
                    Text = strokes.ToString(),
                    FontSize = 13,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = scoreTextColor,
                    HorizontalTextAlignment = TextAlignment.Center,
                    VerticalTextAlignment = TextAlignment.Center
                });

                scoreElement = cellGrid;
            }
            else
            {
                // Blank fallback if the hole hasn't been played yet
                scoreElement = new Label
                {
                    Text = "-",
                    FontSize = 13,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Colors.White,
                    HorizontalTextAlignment = TextAlignment.Center,
                    VerticalTextAlignment = TextAlignment.Center
                };
            }

            targetGrid.Add(scoreElement, col, 2);

            // 4. Putts Label
            string puttsText = hasPutts && putts > 0 ? putts.ToString() : "-";
            if (hasPutts) totalHalfPutts += putts;

            var puttsLabel = new Label
            {
                Text = puttsText,
                FontSize = 11,
                TextColor = Color.FromArgb("#888888"),
                HorizontalTextAlignment = TextAlignment.Center
            };
            targetGrid.Add(puttsLabel, col, 3);
        }

        // --- Totals Column (OUT / IN) ---
        int totalCol = holes.Count;

        targetGrid.Add(new Label
        {
            Text = totalLabel,
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#7C4DFF"),
            HorizontalTextAlignment = TextAlignment.Center
        }, totalCol, 0);

        targetGrid.Add(new Label
        {
            Text = totalHalfPar.ToString(),
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            HorizontalTextAlignment = TextAlignment.Center
        }, totalCol, 1);

        targetGrid.Add(new Label
        {
            Text = totalHalfScore > 0 ? totalHalfScore.ToString() : "-",
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#69F0AE"),
            HorizontalTextAlignment = TextAlignment.Center
        }, totalCol, 2);

        targetGrid.Add(new Label
        {
            Text = totalHalfPutts > 0 ? totalHalfPutts.ToString() : "-",
            FontSize = 11,
            TextColor = Color.FromArgb("#AAAAAA"),
            HorizontalTextAlignment = TextAlignment.Center
        }, totalCol, 3);
    }


    //private async Task<(string hazardType, string side, double distanceYards, double carryYards)> CheckNearestHazardsAsync(Location myLocation)
    //{
    //    if (_holes.Count == 0 || myLocation == null) return (null, null, 0, 0);

    //    var currentHole = _holes[_currentIndex];
    //    var holeFeatures = await _dbService.GetMapFeaturesForHoleAsync(_courseName, currentHole.HoleNumber);

    //    // Filter for hazards only
    //    var hazardFeatures = holeFeatures.Where(f => f.FeatureType == "Bunker" || f.FeatureType == "Water").ToList();
    //    if (hazardFeatures.Count == 0) return (null, null, 0, 0);

    //    double closestDistance = double.MaxValue;
    //    string closestType = null;
    //    string side = "ahead";
    //    double carryYards = 0;

    //    foreach (var feature in hazardFeatures)
    //    {
    //        var rawNodes = System.Text.Json.JsonSerializer.Deserialize<List<MainPage.OverpassGeometry>>(feature.SerializedCoordinates);
    //        if (rawNodes == null || rawNodes.Count == 0) continue;

    //        // Find the closest node of this hazard to the user
    //        foreach (var node in rawNodes)
    //        {
    //            double distMeters = Location.CalculateDistance(
    //                myLocation.Latitude, myLocation.Longitude,
    //                node.lat, node.lon,
    //                DistanceUnits.Kilometers) * 1000.0;

    //            double distYards = distMeters * 1.09361;

    //            if (distYards > 40 && distYards < 230 && distYards < closestDistance)
    //            {
    //                // Verify the hazard is roughly in the direction of the green (within a 75-degree cone)
    //                double hazardBearing = CalculateBearing(myLocation.Latitude, myLocation.Longitude, node.lat, node.lon);
    //                double greenBearing = CalculateBearing(myLocation.Latitude, myLocation.Longitude, currentHole.GreenLatitude, currentHole.GreenLongitude);

    //                double angleDiff = Math.Abs(hazardBearing - greenBearing);
    //                if (angleDiff > 180) angleDiff = 360 - angleDiff;

    //                // If it's within 75 degrees of your target line, it's a real threat
    //                if (angleDiff < 75)
    //                {
    //                    closestDistance = distYards;
    //                    closestType = feature.FeatureType;

    //                    double maxHazardDist = rawNodes.Max(n => Location.CalculateDistance(myLocation.Latitude, myLocation.Longitude, n.lat, n.lon, DistanceUnits.Kilometers) * 1093.61);
    //                    carryYards = Math.Round(maxHazardDist);

    //                    double relativeSideDiff = (hazardBearing - greenBearing + 360) % 360;
    //                    side = (relativeSideDiff > 180) ? "left" : "right";
    //                }
    //            }
    //        }
    //    }

    //    if (closestDistance < 230)
    //    {
    //        return (closestType, side, Math.Round(closestDistance), carryYards);
    //    }

    //    return (null, null, 0, 0);
    //}

    private async Task<string> CheckNearestHazardsAsync(Location myLocation)
    {
        if (_holes.Count == 0 || myLocation == null) return null;

        var currentHole = _holes[_currentIndex];
        var holeFeatures = await _dbService.GetMapFeaturesForHoleAsync(_courseName, currentHole.HoleNumber);

        var hazardFeatures = holeFeatures.Where(f => f.FeatureType == "Bunker" || f.FeatureType == "Water").ToList();
        if (hazardFeatures.Count == 0) return null;

        // NEW: Calculate how far away the center of the green is
        double greenDistMeters = Location.CalculateDistance(
            myLocation.Latitude, myLocation.Longitude,
            currentHole.GreenLatitude, currentHole.GreenLongitude,
            DistanceUnits.Kilometers) * 1000.0;
        double greenDistYards = greenDistMeters * 1.09361;

        // Update the tuple to track if the hazard is past the green
        List<(string Type, string SideText, double Distance, double Carry, bool IsPastGreen)> activeHazards = new();

        foreach (var feature in hazardFeatures)
        {
            var rawNodes = System.Text.Json.JsonSerializer.Deserialize<List<MainPage.OverpassGeometry>>(feature.SerializedCoordinates);
            if (rawNodes == null || rawNodes.Count == 0) continue;

            double featureClosestDist = double.MaxValue;
            double featureFarthestDist = double.MinValue;
            double closestNodeBearing = 0;

            foreach (var node in rawNodes)
            {
                double distMeters = Location.CalculateDistance(
                    myLocation.Latitude, myLocation.Longitude,
                    node.lat, node.lon,
                    DistanceUnits.Kilometers) * 1000.0;

                double distYards = distMeters * 1.09361;

                if (distYards < featureClosestDist)
                {
                    featureClosestDist = distYards;
                    closestNodeBearing = CalculateBearing(myLocation.Latitude, myLocation.Longitude, node.lat, node.lon);
                }
                if (distYards > featureFarthestDist)
                {
                    featureFarthestDist = distYards;
                }
            }

            if (featureClosestDist > 40 && featureClosestDist < 230)
            {
                double greenBearing = CalculateBearing(myLocation.Latitude, myLocation.Longitude, currentHole.GreenLatitude, currentHole.GreenLongitude);

                double angleDiff = Math.Abs(closestNodeBearing - greenBearing);
                if (angleDiff > 180) angleDiff = 360 - angleDiff;

                if (angleDiff < 75)
                {
                    // NEW: Determine if this hazard is pin-high or past the green (giving a 15-yard buffer for the front edge)
                    bool isPastGreen = featureClosestDist > (greenDistYards - 15);

                    double relativeSideDiff = (closestNodeBearing - greenBearing + 360) % 360;
                    string side = (relativeSideDiff > 180) ? "left" : "right";
                    string sideText;

                    // NEW: Change the vocabulary based on if it's past the green
                    if (isPastGreen)
                    {
                        if (angleDiff < 12) sideText = "long";
                        else sideText = $"long and {side}";
                    }
                    else
                    {
                        if (angleDiff < 12) sideText = "straight ahead";
                        else sideText = $"on the {side}";
                    }

                    activeHazards.Add((feature.FeatureType, sideText, Math.Round(featureClosestDist), Math.Round(featureFarthestDist), isPastGreen));
                }
            }
        }

        if (activeHazards.Count == 0) return null;

        activeHazards = activeHazards.OrderBy(h => h.Distance).ToList();

        List<string> warnings = new List<string>();
        foreach (var hazard in activeHazards)
        {
            // NEW: If it's past the green, drop the carry yardage entirely!
            if (hazard.IsPastGreen)
            {
                warnings.Add($"{hazard.Type} {hazard.SideText}, starting at {hazard.Distance} yards");
            }
            else
            {
                if (hazard.Type == "Water")
                {
                    warnings.Add($"Water {hazard.SideText}, starting {hazard.Distance} yards away ({hazard.Carry} yards to carry)");
                }
                else if (hazard.Type == "Bunker")
                {
                    warnings.Add($"Bunker {hazard.SideText} at {hazard.Distance} yards ({hazard.Carry} yards to carry)");
                }
            }
        }

        return string.Join(". Also, ", warnings) + ". ";
    }
    private async void OnClubSelected_Clicked(object sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is GolfClub selectedClub)
        {
            ClubSelectionOverlay.IsVisible = false;
            await _dbService.SaveShotAsync(selectedClub.Id, _pendingShotDistance);
        }
    }

    private void OnSkipClub_Clicked(object sender, EventArgs e)
    {
        ClubSelectionOverlay.IsVisible = false;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        if (_weatherRefreshTimer != null)
        {
            _weatherRefreshTimer.Stop();
        }

        if (_isTracking)
        {
            Geolocation.LocationChanged -= Geolocation_LocationChanged;
            Geolocation.StopListeningForeground();
            _isTracking = false;

#if ANDROID
            var intent = new Android.Content.Intent(Android.App.Application.Context, typeof(AutoCaddie.Platforms.Android.GpsForegroundService));
            intent.SetAction("STOP_SERVICE");
            Android.App.Application.Context.StartService(intent);
#endif
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

            if (_roundScores[currentHole.HoleNumber] < 15)
            {
                _roundScores[currentHole.HoleNumber]++;
                ScoreLabel.Text = _roundScores[currentHole.HoleNumber].ToString();
            }

            UpdateRoundScoreToPar();
        }
    }

    private async void OnUndoShot_Clicked(object sender, EventArgs e)
    {
        if (_holes.Count == 0) return;

        var currentHole = _holes[_currentIndex];

        // Ensure we don't drop below 1 stroke
        if (_roundScores.ContainsKey(currentHole.HoleNumber) && _roundScores[currentHole.HoleNumber] > 1)
        {
            _roundScores[currentHole.HoleNumber]--;
            ScoreLabel.Text = _roundScores[currentHole.HoleNumber].ToString();

            // Refresh the live leader board math
            UpdateRoundScoreToPar();

            // Tell the database service to remove the last recorded shot log for this round/club
            await _dbService.UndoLastShotAsync();

            await DisplayAlert("Shot Undone", "Last registered stroke has been removed.", "OK");
        }
        else
        {
            await DisplayAlert("Notice", "Cannot go below 1 stroke on a hole.", "OK");
        }
    }
    private void OnMinusPutt_Clicked(object sender, EventArgs e)
    {
        if (_holes.Count == 0) return;
        var currentHole = _holes[_currentIndex];

        if (_roundPutts[currentHole.HoleNumber] > 0)
        {
            _roundPutts[currentHole.HoleNumber]--;
           
            PuttLabel.Text = _roundPutts[currentHole.HoleNumber].ToString();

            if (_roundScores[currentHole.HoleNumber] > 1)
            {
                _roundScores[currentHole.HoleNumber]--;
                ScoreLabel.Text = _roundScores[currentHole.HoleNumber].ToString();
            }
            UpdateRoundScoreToPar();
        }
    }

    private async void OnToggleArcs_Clicked(object sender, EventArgs e)
    {
        _showLayupArcs = !_showLayupArcs;

        // Dim the icon to 40% opacity when turned off
        ArcsIconPath.Opacity = _showLayupArcs ? 1.0 : 0.4;

        // Redraw the map to either inject or remove the circles
        await DisplayCurrentHoleAsync();
    }

    public bool IsPointInPolygon(Microsoft.Maui.Devices.Sensors.Location point, List<Microsoft.Maui.Devices.Sensors.Location> polygon)
    {
        if (polygon == null || polygon.Count < 3) return false;

        bool isInside = false;
        int j = polygon.Count - 1;

        for (int i = 0; i < polygon.Count; i++)
        {
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
                var rawNodes = System.Text.Json.JsonSerializer.Deserialize<List<MainPage.OverpassGeometry>>(fairwayFeature.SerializedCoordinates);
                var fairwayNodes = rawNodes.Select(n => new Location(n.lat, n.lon)).ToList();
                bool hitFairway = IsPointInPolygon(_lastKnownLocation, fairwayNodes);
                _roundFairways[currentHole.HoleNumber] = hitFairway;
            }
        }
    }

    private async Task<double?> FetchElevationAsync(double lat, double lon)
    {
        try
        {
            using HttpClient client = new HttpClient();
            string url = $"https://api.open-meteo.com/v1/elevation?latitude={lat}&longitude={lon}";
            var response = await client.GetStringAsync(url);
            var data = System.Text.Json.JsonSerializer.Deserialize<OpenMeteoElevationResponse>(response, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (data?.elevation != null && data.elevation.Length > 0)
            {
                return data.elevation[0];
            }
        }
        catch
        {
            // Fails silently if no cell service
        }
        return null;
    }

    private async void InjectFakeTestData()
    {
        var bag = await _dbService.GetMyBagAsync();

        foreach (var club in bag)
        {
            club.ShotCount = 5;

            if (club.Name == "Dr") club.TotalYardage = 240 * 5;
            if (club.Name == "3w") club.TotalYardage = 215 * 5;
            if (club.Name == "5i") club.TotalYardage = 175 * 5;
            if (club.Name == "7i") club.TotalYardage = 150 * 5;
            if (club.Name == "9i") club.TotalYardage = 125 * 5;
            if (club.Name == "PW") club.TotalYardage = 110 * 5;
        }
        _myBag = bag;
    }

    // --- JSON CLASSES REQUIRED FOR THE BACKGROUND WEATHER REFRESH ---
    public class OpenMeteoMapResponse
    {
        public CurrentMapWeather current { get; set; }
    }

    public class CurrentMapWeather
    {
        public double temperature_2m { get; set; }
        public int weather_code { get; set; }
        public double wind_speed_10m { get; set; }
        public double wind_direction_10m { get; set; }
    }
    // JSON Class for Elevation
    public class OpenMeteoElevationResponse
    {
        public double[] elevation { get; set; }
    }
}