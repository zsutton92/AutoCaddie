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
            await LoadSavedCoursesFromDbAsync();
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
                        FavoriteIcon = isFav ? "star_filled.png" : "star_outline.png"
                    });
                }

                SavedHeaderLabel.IsVisible = true;
            }
            else
            {
                SavedHeaderLabel.IsVisible = false;
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

        // Fires when selecting a course from SEARCH RESULTS
        private async void OnCourseSelected(object sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is CourseSearchResult selectedCourse)
            {
                ResultsList.SelectedItem = null;

                var dbService = new AutoCaddie.Services.DatabaseService();
                var existingHoles = await dbService.GetHolesForCourseAsync(selectedCourse.Name);

                // 1. CACHE CHECK: If holes exist locally, skip API download entirely!
                if (existingHoles != null && existingHoles.Count > 0)
                {
                    await Navigation.PushAsync(new CoursePage(selectedCourse.Name));
                    return;
                }

                // 2. Otherwise download course data from API
                CourseSearchBar.Text = $"Downloading {selectedCourse.Name}...";
                await DownloadCourseDataAsync(selectedCourse);
                CourseSearchBar.Text = string.Empty;
            }
        }

        // Fires when selecting a course from MY COURSES (Local Database)
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
                    await Navigation.PushAsync(new CoursePage(course.Name));
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

        //private async Task SaveDownloadedDataToDb(string courseName, OverpassResponse overpassData)
        //{
        //    var dbService = new AutoCaddie.Services.DatabaseService();

        //    foreach (var element in overpassData.elements)
        //    {
        //        if (element.tags != null && element.tags.ContainsKey("golf") && element.tags["golf"] == "hole" && element.geometry != null && element.geometry.Count >= 2)
        //        {
        //            var tee = element.geometry.First();
        //            var green = element.geometry.Last();

        //            var hole = new AutoCaddie.Models.GolfHole
        //            {
        //                CourseName = courseName,
        //                HoleNumber = int.TryParse(element.tags.GetValueOrDefault("ref"), out int refNum) ? refNum : 0,
        //                Par = int.TryParse(element.tags.GetValueOrDefault("par"), out int parNum) ? parNum : 4,
        //                TeeLatitude = tee.lat,
        //                TeeLongitude = tee.lon,
        //                GreenLatitude = green.lat,
        //                GreenLongitude = green.lon,
        //                IsFavorite = false
        //            };

        //            await dbService.SaveHoleAsync(hole);
        //        }
        //    }
        //}

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
                        feature.TeeColor = element.tags.GetValueOrDefault("colour", "white");
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

                    // Save the newly categorized feature to the database
                    // (You will need to add SaveMapFeatureAsync to your DatabaseService)
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
            public string FavoriteIcon { get; set; } = "star_outline.png";
        }

        public class OverpassResponse
        {
            public List<OverpassElement> elements { get; set; }
        }

        public class OverpassElement
        {
            public long id { get; set; }
            public Dictionary<string, string> tags { get; set; }
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