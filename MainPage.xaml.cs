using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using Microsoft.Maui.Devices.Sensors;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Linq;

namespace AutoCaddie
{
    public partial class MainPage : ContentPage
    {
        public ObservableCollection<CourseSearchResult> SearchResults { get; set; } = new();
        public ObservableCollection<CourseSearchResult> SavedCourses { get; set; } = new();

        public MainPage()
        {
            InitializeComponent();

            ResultsList.ItemsSource = SearchResults;
            SavedCoursesList.ItemsSource = SavedCourses;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await RequestLocationPermissionOnStartup();
            await LoadSavedCoursesFromDbAsync();
        }
        private async Task RequestLocationPermissionOnStartup()
        {
            var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();

            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            }

            if (status != PermissionStatus.Granted)
            {
                // Optional: A friendly nudge if they deny it on startup
                await DisplayAlert("GPS Required", "AutoCaddie needs location access to give you accurate yardages on the course.", "Got it");
            }
        }

        private async void OnMyBag_Clicked(object sender, EventArgs e)
        {
            await Navigation.PushAsync(new MyBagPage());
        }
        // Loads courses already saved in local SQLite database
        private async Task LoadSavedCoursesFromDbAsync()
        {
            var dbService = new AutoCaddie.Services.DatabaseService();
            var allHoles = await dbService.GetHolesAsync();

            SavedCourses.Clear();

            if (allHoles != null && allHoles.Count > 0)
            {
                var groupedCourses = allHoles.GroupBy(h => h.CourseName);

                foreach (var group in groupedCourses)
                {
                    bool isFav = group.Any(h => h.IsFavorite);

                    SavedCourses.Add(new CourseSearchResult
                    {
                        Name = group.Key,
                        LocationDetails = $"{group.Count()} Holes Mapped",
                        IsFavorite = isFav,
                        //FavoriteIcon = isFav ? "star_filled.png" : "star_outline.png"
                    });
                }

                SavedHeaderLabel.IsVisible = true;
            }
            else
            {
                SavedHeaderLabel.IsVisible = false;
            }
        }
        private async void OnUpdateCourseMap_Invoked(object sender, EventArgs e)
        {
            if (sender is SwipeItem item && item.CommandParameter is CourseSearchResult course)
            {
                bool confirm = await DisplayAlert("Update Map", $"Do you want to re-download the latest OSM mapping data for {course.Name}?", "Yes, Update", "Cancel");

                if (confirm)
                {
                    CourseSearchBar.Text = $"Updating {course.Name} maps...";

                    var dbService = new AutoCaddie.Services.DatabaseService();
                    var existingHoles = await dbService.GetHolesForCourseAsync(course.Name);

                    if (existingHoles != null && existingHoles.Count > 0)
                    {
                        // Grab the center coordinates from the first hole we have saved
                        double centerLat = existingHoles.First().TeeLatitude;
                        double centerLon = existingHoles.First().TeeLongitude;

                        // 1. Wipe the old course data
                        await dbService.DeleteCourseAsync(course.Name);

                        // 2. Package it into a search result object so DownloadCourseDataAsync can use it
                        var searchObj = new CourseSearchResult
                        {
                            Name = course.Name,
                            Latitude = centerLat,
                            Longitude = centerLon
                        };

                        // 3. Re-download fresh data from Overpass
                        await DownloadCourseDataAsync(searchObj);
                    }
                    else
                    {
                        await DisplayAlert("Error", "Could not find reference coordinates for this course.", "OK");
                    }

                    CourseSearchBar.Text = string.Empty;
                }
            }
        }

        private async void OnDeleteCourse_Clicked(object sender, EventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is AutoCaddie.MainPage.CourseSearchResult course)
            {
                bool confirm = await DisplayAlert("Delete Course", $"Are you sure you want to remove {course.Name} and all its map data from your device?", "Yes, Delete", "Cancel");

                if (confirm)
                {
                    var dbService = new AutoCaddie.Services.DatabaseService();
                    await dbService.DeleteCourseAsync(course.Name);
                    await LoadSavedCoursesFromDbAsync();
                }
            }
        }



        // Fires when search is submitted
        private async void OnSearchButtonPressed(object sender, EventArgs e)
        {
            string searchText = CourseSearchBar.Text;
            if (string.IsNullOrWhiteSpace(searchText)) return;

            SearchResults.Clear();
            SearchHeaderLabel.IsVisible = true;
            CourseSearchBar.Placeholder = "Searching...";
            CourseSearchBar.Text = string.Empty;

            string googleApiKey = "AIzaSyBlwx1J1y0gerb3q7wIlUlRlZF0O3cPNSc";

            try
            {
                using HttpClient client = new HttpClient();
                client.DefaultRequestHeaders.Add("X-Goog-Api-Key", googleApiKey);
                client.DefaultRequestHeaders.Add("X-Goog-FieldMask", "places.displayName,places.formattedAddress,places.location");

                var requestBody = new { textQuery = $"{searchText} golf course" };
                string jsonBody = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

                string url = "https://places.googleapis.com/v1/places:searchText";

                HttpResponseMessage response = await client.PostAsync(url, content);
                response.EnsureSuccessStatusCode();

                string jsonResponse = await response.Content.ReadAsStringAsync();

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var googleData = JsonSerializer.Deserialize<GooglePlacesResponse>(jsonResponse, options);

                if (googleData?.places != null && googleData.places.Count > 0)
                {
                    foreach (var place in googleData.places)
                    {
                        SearchResults.Add(new CourseSearchResult
                        {
                            Name = place.displayName.text,
                            LocationDetails = place.formattedAddress,
                            Latitude = place.location.latitude,
                            Longitude = place.location.longitude
                        });
                    }
                }
                else
                {
                    await DisplayAlert("No Courses", "No golf courses found near this location.", "OK");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Google API Error: {ex.Message}");
                await DisplayAlert("Search Error", $"Failed: {ex.Message}", "OK");
            }

            CourseSearchBar.Placeholder = "Search for a golf course...";
        }
        

        private async Task OpenCourseAsync(CourseSearchResult course)
        {
            if (course == null) return;

            var dbService = new AutoCaddie.Services.DatabaseService();
            var existingHoles = await dbService.GetHolesForCourseAsync(course.Name);

            // 1. If it's a new course, show loading overlay and download
            if (existingHoles == null || existingHoles.Count == 0)
            {
                LoadingText.Text = $"Downloading {course.Name}...";
                LoadingOverlay.IsVisible = true;
                DownloadSpinner.IsRunning = true;

                try
                {
                    await DownloadCourseDataAsync(course);
                    await LoadSavedCoursesFromDbAsync();
                }
                catch (Exception)
                {
                    await DisplayAlert("Download Failed", "There was an issue downloading the course map.", "OK");
                    return;
                }
                finally
                {
                    LoadingOverlay.IsVisible = false;
                    DownloadSpinner.IsRunning = false;
                    CourseSearchBar.Text = string.Empty;
                }
            }

            // 2. Simply launch the Hub!
            await Navigation.PushAsync(new CourseDetailsPage(course.Name));
        }
        private async void OnCourseCard_Tapped(object sender, EventArgs e)
        {
            if (sender is View view && view.BindingContext is CourseSearchResult selectedCourse)
            {
                ResultsList.SelectedItem = null;
                await OpenCourseAsync(selectedCourse);
            }
        }

        private async void OnCourseSelected(object sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is CourseSearchResult selectedCourse)
            {
                ResultsList.SelectedItem = null;
                await OpenCourseAsync(selectedCourse);
            }
        }
       
        // Fires when selecting a course from MY COURSES(Local Database)
        private async void OnSavedCourseSelected(object sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is CourseSearchResult selectedCourse)
            {
                SavedCoursesList.SelectedItem = null;
                // Instantly open without network calls!
                await Navigation.PushAsync(new CoursePage(selectedCourse.Name));
            }
        }

        private async Task DownloadCourseDataAsync(CourseSearchResult course)
        {
            try
            {
                using HttpClient client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "AutoCaddie_DevelopmentApp/1.0");

                string lat = course.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string lon = course.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);

              
                string query = $"[out:json][timeout:15];(" +
                               $"nwr(around:1500,{lat},{lon})[\"golf\"=\"hole\"];" +
                               $"nwr(around:1500,{lat},{lon})[\"golf\"=\"green\"];" +
                               $"nwr(around:1500,{lat},{lon})[\"golf\"=\"tee\"];" +
                               $"nwr(around:1500,{lat},{lon})[\"golf\"=\"fairway\"];" +
                               $"nwr(around:1500,{lat},{lon})[\"golf\"=\"bunker\"];" +
                               $"nwr(around:1500,{lat},{lon})[\"golf\"=\"water_hazard\"];" +
                               $"nwr(around:1500,{lat},{lon})[\"natural\"=\"water\"];" +
                               $");out geom tags;";


                var content = new StringContent(query, System.Text.Encoding.UTF8, "text/plain");

                string[] overpassServers = new string[]
                {
            "https://lz4.overpass-api.de/api/interpreter",
            "https://z.overpass-api.de/api/interpreter",
            "https://overpass.kumi.systems/api/interpreter"
                };

                string jsonResponse = null;

                foreach (var url in overpassServers)
                {
                    try
                    {
                        HttpResponseMessage response = await client.PostAsync(url, content);
                        if (response.IsSuccessStatusCode)
                        {
                            jsonResponse = await response.Content.ReadAsStringAsync();
                            break;
                        }
                    }
                    catch (Exception) { }
                }

                if (string.IsNullOrEmpty(jsonResponse))
                {
                    await DisplayAlert("API Error", "All mapping servers are currently busy or blocked. Please try again.", "OK");
                    return;
                }

                // Catch rate limits or HTML error pages sent by the server
                if (!jsonResponse.TrimStart().StartsWith("{"))
                {
                    await DisplayAlert("Server Blocked", $"The server rejected the request:\n\n{jsonResponse}", "OK");
                    return;
                }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var overpassData = JsonSerializer.Deserialize<OverpassResponse>(jsonResponse, options);

                // Verify we actually found holes before trying to navigate
                if (overpassData?.elements != null && overpassData.elements.Count > 0)
                {
                    await SaveDownloadedDataToDb(course.Name, overpassData);
                    await LoadSavedCoursesFromDbAsync();
                   // await Navigation.PushAsync(new CoursePage(course.Name));
                }
                else
                {
                    await DisplayAlert("No Holes Found", $"The map server found the location, but there is no valid golf hole data mapped within 1500 meters of {course.Name}.", "OK");
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("API Error", $"Could not process data: {ex.Message}", "OK");
            }
        }
        

        private async Task SaveDownloadedDataToDb(string courseName, OverpassResponse overpassData)
        {
            if (overpassData?.elements == null) return;

            var dbService = new AutoCaddie.Services.DatabaseService();

            // We will still save the main GolfHole for backwards compatibility with your scorecard
            // But we will also start saving the detailed map features
            foreach (var element in overpassData.elements)
            {
                if (element.tags == null || element.geometry == null || element.geometry.Count == 0)
                    continue;

                string golfTag = element.tags.GetValueOrDefault("golf", "").ToLower();
                string naturalTag = element.tags.GetValueOrDefault("natural", "").ToLower();
                string refTag = element.tags.GetValueOrDefault("ref", "0");

                // Handle elements shared between holes (e.g. ref="2;3")
                var associatedHoles = refTag.Split(';')
                                            .Select(r => int.TryParse(r.Trim(), out int h) ? h : 0)
                                            .ToList();

                // Serialize the geometry list into a JSON string for SQLite storage
                string jsonGeom = System.Text.Json.JsonSerializer.Serialize(element.geometry);

                foreach (int holeNum in associatedHoles)
                {
                    var feature = new AutoCaddie.Models.CourseMapFeature
                    {
                        CourseName = courseName,
                        HoleNumber = holeNum,
                        SerializedCoordinates = jsonGeom
                    };

                    // 1. Process Main Hole Line (For your Scorecard Par info)
                    if (golfTag == "hole")
                    {
                        feature.FeatureType = "HoleLine";

                        // Preserve your original GolfHole creation here so the UI doesn't break
                        var tee = element.geometry.First();
                        var green = element.geometry.Last();
                        int par = int.TryParse(element.tags.GetValueOrDefault("par"), out int p) ? p : 4;

                        var oldHoleModel = new AutoCaddie.Models.GolfHole
                        {
                            CourseName = courseName,
                            HoleNumber = holeNum,
                            Par = par,
                            TeeLatitude = tee.lat,
                            TeeLongitude = tee.lon,
                            GreenLatitude = green.lat,
                            GreenLongitude = green.lon
                        };
                        await dbService.SaveHoleAsync(oldHoleModel);
                    }
                    // 2. Process Greens
                    else if (golfTag == "green")
                    {
                        feature.FeatureType = "Green";
                    }
                    // 3. Process Tees
                    else if (golfTag == "tee")
                    {
                        feature.FeatureType = "Tee";
                        feature.TeeColor = element.tags.GetValueOrDefault("colour", null);
                    }
                    // 4. Process Fairways
                    else if (golfTag == "fairway")
                    {
                        feature.FeatureType = "Fairway";
                    }
                    // 5. Process Bunkers
                    else if (golfTag == "bunker")
                    {
                        feature.FeatureType = "Bunker";
                    }
                    // 6. Process Water
                    else if (golfTag == "water_hazard" || naturalTag == "water")
                    {
                        feature.FeatureType = "Water";
                    }
                    else
                    {
                        // Unrecognized tag, skip saving
                        continue;
                    }

                    
                    await dbService.SaveMapFeatureAsync(feature);
                }
            }
        }

       
        // Toggles a course as a Favorite / Home course
        private async void OnFavoriteToggle_Clicked(object sender, EventArgs e)
        {
            if (sender is ImageButton button && button.CommandParameter is CourseSearchResult course)
            {
                var dbService = new AutoCaddie.Services.DatabaseService();
                var holes = await dbService.GetHolesForCourseAsync(course.Name);

                bool newFavoriteState = !course.IsFavorite;

                foreach (var hole in holes)
                {
                    hole.IsFavorite = newFavoriteState;
                    await dbService.SaveHoleAsync(hole);
                }

                await LoadSavedCoursesFromDbAsync();
            }
        }

        private async void OnHistory_Clicked(object sender, EventArgs e)
        {
            await Navigation.PushAsync(new RoundHistoryPage());
        }

        // ---------------- API & UI MODELS ---------------- //

        public class CourseSearchResult
        {
            public string Name { get; set; }
            public string LocationDetails { get; set; }
            public double Latitude { get; set; }
            public double Longitude { get; set; }
            public bool IsFavorite { get; set; }

            public Color StarFill => IsFavorite ? Color.FromArgb("#FFC107") : Colors.Transparent; // Gold when active, clear when empty
            public Color StarStroke => IsFavorite ? Color.FromArgb("#FFA000") : Color.FromArgb("#7C4DFF"); // Deep gold or purple outline
          
        }

        public class OverpassResponse
        {
            public List<OverpassElement> elements { get; set; }
        }

        public class OverpassElement
        {
            public string type { get; set; }
            public long id { get; set; }
            public Dictionary<string, string> tags { get; set; }
            public List<OverpassGeometry> geometry { get; set; }
            public List<OverpassMember> members { get; set; }
        }

        public class OverpassMember
        {
            public string type { get; set; }
            public string role { get; set; } // This will hold "inner" or "outer"
            public List<OverpassGeometry> geometry { get; set; }
        }
        public class OverpassGeometry
        {
            public double lat { get; set; }
            public double lon { get; set; }
        }

        public class GooglePlacesResponse
        {
            public List<GooglePlace> places { get; set; }
        }

        public class GooglePlace
        {
            public GoogleDisplayName displayName { get; set; }
            public string formattedAddress { get; set; }
            public GoogleLocation location { get; set; }
        }

        public class GoogleDisplayName
        {
            public string text { get; set; }
        }

        public class GoogleLocation
        {
            public double latitude { get; set; }
            public double longitude { get; set; }
        }
    }
}