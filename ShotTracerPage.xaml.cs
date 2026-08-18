using AutoCaddie.Models;
using AutoCaddie.Services;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;

namespace AutoCaddie;

public partial class ShotTracerPage : ContentPage
{
    private DatabaseService _dbService = new();
    private GolfRound _round;
    private int _holeNumber;

    public ShotTracerPage(GolfRound round, int holeNumber)
    {
        InitializeComponent();
        _round = round;
        _holeNumber = holeNumber;

        HoleTitleLabel.Text = $"Hole {_holeNumber} Tracer";

        TracerMap.Loaded += async (s, e) => await LoadTracerDataAsync();
    }

    private async Task LoadTracerDataAsync()
    {
        // 1. Fetch Course Features, Hole Data, & Shot Data
        var holes = await _dbService.GetHolesForCourseAsync(_round.CourseName);
        var currentHole = holes.FirstOrDefault(h => h.HoleNumber == _holeNumber);

        if (currentHole == null) return;

        var holeFeatures = await _dbService.GetMapFeaturesForHoleAsync(_round.CourseName, _holeNumber);
        var allShots = await _dbService.GetShotLocationsForRoundAsync(_round.Id);

        // Filter shots strictly for the hole we tapped on, ordered sequentially
        var holeShots = allShots.Where(s => s.HoleNumber == _holeNumber).OrderBy(s => s.StrokeNumber).ToList();

        // 2. Draw the Course Polygons (Fairways, Greens, Hazards)
        foreach (var feature in holeFeatures)
        {
            var rawNodes = System.Text.Json.JsonSerializer.Deserialize<List<MapNode>>(feature.SerializedCoordinates);
            if (rawNodes == null || rawNodes.Count == 0) continue;

            var polygon = new Microsoft.Maui.Controls.Maps.Polygon { StrokeWidth = 2 };

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

            var rawLocations = rawNodes.Select(n => new Location(n.lat, n.lon)).ToList();
            var smoothedLocations = SmoothPolygon(rawLocations, 2);

            foreach (var loc in smoothedLocations)
            {
                polygon.Geopath.Add(loc);
            }

            TracerMap.MapElements.Add(polygon);
        }

        // 3. Draw the Shot Tracer Polyline
        if (holeShots.Count > 0)
        {
            var tracerLine = new Polyline
            {
                StrokeColor = Color.FromArgb("#FFC107"), // Bright Gold/Yellow
                StrokeWidth = 4
            };

            // Add the line first so the dots render ON TOP of the line
            foreach (var shot in holeShots)
            {
                tracerLine.Geopath.Add(new Location(shot.Latitude, shot.Longitude));
            }
            TracerMap.MapElements.Add(tracerLine);

            // Draw a clean blue dot for each shot instead of a big red pin
            for (int i = 0; i < holeShots.Count; i++)
            {
                var shot = holeShots[i];
                var loc = new Location(shot.Latitude, shot.Longitude);

                var shotDot = new Circle
                {
                    Center = loc,
                    Radius = Distance.FromMeters(2.5), // A physical 8-foot circle on the map
                    FillColor = Color.FromArgb("#2196F3"), // Dodger Blue
                    StrokeColor = Colors.White,
                    StrokeWidth = 2
                };

                TracerMap.MapElements.Add(shotDot);
            }

            ShotDetailsLabel.Text = $"Successfully mapped {holeShots.Count} shots.";
        }
        else
        {
            ShotDetailsLabel.Text = "No GPS tracking data found for this hole.";
        }

        // 4. Cinematic Camera Calculation (Tee at bottom, Green at top)
        double activeTeeLat = currentHole.TeeLatitude;
        double activeTeeLon = currentHole.TeeLongitude;

        double midLat = (activeTeeLat + currentHole.GreenLatitude) / 2;
        double midLon = (activeTeeLon + currentHole.GreenLongitude) / 2;

        double bearing = CalculateBearing(activeTeeLat, activeTeeLon, currentHole.GreenLatitude, currentHole.GreenLongitude);
        double distanceMeters = Location.CalculateDistance(activeTeeLat, activeTeeLon, currentHole.GreenLatitude, currentHole.GreenLongitude, DistanceUnits.Kilometers) * 1000.0;

        if (distanceMeters < 50) distanceMeters = 50;

#if ANDROID
        var mapHandler = TracerMap.Handler as Microsoft.Maui.Maps.Handlers.MapHandler;
        if (mapHandler?.Map is Android.Gms.Maps.GoogleMap googleMap)
        {
            googleMap.UiSettings.MyLocationButtonEnabled = false;
            googleMap.UiSettings.ZoomControlsEnabled = false;
            googleMap.UiSettings.CompassEnabled = false;
            googleMap.UiSettings.MapToolbarEnabled = false;

            float perfectZoom = CalculatePerfectZoom(midLat, distanceMeters, TracerMap.Height);

            var cinematicCamera = new Android.Gms.Maps.Model.CameraPosition.Builder()
                .Target(new Android.Gms.Maps.Model.LatLng(midLat, midLon))
                .Zoom(perfectZoom)
                .Bearing((float)bearing)
                .Build();

            googleMap.AnimateCamera(Android.Gms.Maps.CameraUpdateFactory.NewCameraPosition(cinematicCamera));
        }
#else
        var centerPosition = new Location(midLat, midLon);
        TracerMap.MoveToRegion(MapSpan.FromCenterAndRadius(centerPosition, Distance.FromMeters(distanceMeters / 1.5)));
#endif
    }

    // --- Math & Helper Methods ---
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
                var p2 = smoothed[(j + 1) % smoothed.Count];
                temp.Add(new Location(0.75 * p1.Latitude + 0.25 * p2.Latitude, 0.75 * p1.Longitude + 0.25 * p2.Longitude));
                temp.Add(new Location(0.25 * p1.Latitude + 0.75 * p2.Latitude, 0.25 * p1.Longitude + 0.75 * p2.Longitude));
            }
            smoothed = temp;
        }
        return smoothed;
    }

    public class MapNode
    {
        public double lat { get; set; }
        public double lon { get; set; }
    }
}