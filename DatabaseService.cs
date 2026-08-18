using System;
using System.Collections.Generic;
using System.Text;
using SQLite;
using AutoCaddie.Models;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Maui.Devices.Sensors;

namespace AutoCaddie.Services;

public class DatabaseService
{
    private SQLiteAsyncConnection _db;

    async Task Init()
    {
        if (_db is not null) return;

        var dbPath = Path.Combine(FileSystem.AppDataDirectory, "AutoCaddie.db3");
        _db = new SQLiteAsyncConnection(dbPath);

        // This automatically creates the table if it doesn't exist
        await _db.CreateTableAsync<GolfHole>();
        await _db.CreateTableAsync<GolfRound>();
        await _db.CreateTableAsync<HoleScore>();

        await _db.CreateTableAsync<GolfClub>();
        await _db.CreateTableAsync<TrackedShot>();
        await _db.CreateTableAsync<AutoCaddie.Models.CourseMapFeature>();

        // NEW: Create the table for our shot tracers
        await _db.CreateTableAsync<AutoCaddie.Models.RoundShotLocation>();

        var clubCount = await _db.Table<GolfClub>().CountAsync();
        if (clubCount == 0)
        {
            var myBag = new List<GolfClub>
            {
                new GolfClub { Name = "Dr" },
                new GolfClub { Name = "3w" },
                new GolfClub { Name = "5w" },
                new GolfClub { Name = "3h" },
                new GolfClub { Name = "4h" },
                new GolfClub { Name = "5h" },
                new GolfClub { Name = "4i" },
                new GolfClub { Name = "5i" },
                new GolfClub { Name = "6i" },
                new GolfClub { Name = "7i" },
                new GolfClub { Name = "8i" },
                new GolfClub { Name = "9i" },
                new GolfClub { Name = "PW" },
                new GolfClub { Name = "GW" },
                new GolfClub { Name = "SW" },
                new GolfClub { Name = "LW" },
                new GolfClub { Name = "Ptt" }
            };
            await _db.InsertAllAsync(myBag);
        }
    }

    public async Task SaveClubAsync(GolfClub club)
    {
        await Init();
        if (club.Id != 0)
        {
            await _db.UpdateAsync(club);
        }
        else
        {
            await _db.InsertAsync(club);
        }
    }

    public async Task DeleteClubAsync(GolfClub club)
    {
        await Init();
        await _db.DeleteAsync(club);
    }

    public async Task SaveMapFeatureAsync(AutoCaddie.Models.CourseMapFeature feature)
    {
        await Init();
        await _db.InsertAsync(feature);
    }

    public async Task<List<AutoCaddie.Models.CourseMapFeature>> GetMapFeaturesForCourseAsync(string courseName)
    {
        await Init();
        return await _db.Table<AutoCaddie.Models.CourseMapFeature>()
                        .Where(f => f.CourseName == courseName)
                        .ToListAsync();
    }

    public async Task<List<AutoCaddie.Models.CourseMapFeature>> GetMapFeaturesForHoleAsync(string courseName, int holeNumber)
    {
        await Init();
        return await _db.Table<AutoCaddie.Models.CourseMapFeature>()
                    .Where(f => f.CourseName == courseName &&
                          (f.HoleNumber == holeNumber ||
                          (f.HoleNumber == 0 && f.FeatureType != "Water")))
                    .ToListAsync();
    }

    public async Task UndoLastShotAsync()
    {
        await Init();

        var lastShot = await _db.Table<TrackedShot>()
                                      .OrderByDescending(s => s.Id)
                                      .FirstOrDefaultAsync();
        if (lastShot != null)
        {
            await _db.DeleteAsync(lastShot);
        }
    }
    public async Task<List<GolfHole>> GetHolesForCourseAsync(string courseName)
    {
        await Init();
        return await _db.Table<GolfHole>()
                        .Where(h => h.CourseName == courseName)
                        .ToListAsync();
    }

    public async Task<List<GolfHole>> GetHolesAsync()
    {
        await Init();
        return await _db.Table<GolfHole>().ToListAsync();
    }

    public async Task<int> SaveHoleAsync(GolfHole hole)
    {
        await Init();
        if (hole.Id != 0)
        {
            return await _db.UpdateAsync(hole);
        }
        else
        {
            return await _db.InsertAsync(hole);
        }
    }


    // UPDATED: Added shotLocations dictionary parameter
    public async Task SaveCompletedRoundAsync(string courseName, List<AutoCaddie.Models.GolfHole> holes, Dictionary<int, int> scores, Dictionary<int, int> putts, Dictionary<int, bool?> fairways, Dictionary<int, List<Location>> shotLocations)
    {
        await Init();

        // 1. Sum up the totals for the main round summary
        int totalScore = scores.Values.Sum();
        int totalPutts = putts.Values.Sum();
        int totalGIR = 0;
        int totalFIR = 0;

        // 2. Tally up the FIR and GIR before saving the round
        foreach (var hole in holes)
        {
            int holeScore = scores.ContainsKey(hole.HoleNumber) ? scores[hole.HoleNumber] : hole.Par;
            int holePutts = putts.ContainsKey(hole.HoleNumber) ? putts[hole.HoleNumber] : 0;
            bool? hitFairway = fairways.ContainsKey(hole.HoleNumber) ? fairways[hole.HoleNumber] : null;

            if ((holeScore - holePutts) <= (hole.Par - 2)) totalGIR++;
            if (hitFairway == true) totalFIR++;
        }

        // 3. Create and save the ONE summary record for the entire round
        var newRound = new AutoCaddie.Models.GolfRound
        {
            CourseName = courseName,
            DatePlayed = DateTime.Now,
            TotalScore = totalScore,
            TotalPutts = totalPutts,
            TotalGIR = totalGIR,
            TotalFIR = totalFIR
        };

        await _db.InsertAsync(newRound);

        // 4. Loop through again to save the 18 individual hole records
        foreach (var hole in holes)
        {
            int holeScore = scores.ContainsKey(hole.HoleNumber) ? scores[hole.HoleNumber] : hole.Par;
            int holePutts = putts.ContainsKey(hole.HoleNumber) ? putts[hole.HoleNumber] : 0;
            bool? hitFairway = fairways.ContainsKey(hole.HoleNumber) ? fairways[hole.HoleNumber] : null;

            var scoreRecord = new AutoCaddie.Models.HoleScore
            {
                RoundId = newRound.Id,
                HoleNumber = hole.HoleNumber,
                Par = hole.Par,
                Strokes = holeScore,
                Putts = holePutts,
                HitFairway = hitFairway
            };

            await _db.InsertAsync(scoreRecord);
        }

        // 5. NEW: Save all the captured GPS coordinates for the tracer lines
        if (shotLocations != null)
        {
            foreach (var kvp in shotLocations)
            {
                int holeNum = kvp.Key;
                var locations = kvp.Value;
                int strokeIndex = 1;

                foreach (var loc in locations)
                {
                    var shotLoc = new AutoCaddie.Models.RoundShotLocation
                    {
                        RoundId = newRound.Id,
                        HoleNumber = holeNum,
                        StrokeNumber = strokeIndex,
                        Latitude = loc.Latitude,
                        Longitude = loc.Longitude
                    };

                    await _db.InsertAsync(shotLoc);
                    strokeIndex++;
                }
            }
        }
    }

    public async Task<List<HoleScore>> GetScoresForRoundAsync(int roundId)
    {
        await Init();
        return await _db.Table<HoleScore>()
                        .Where(h => h.RoundId == roundId)
                        .OrderBy(h => h.HoleNumber)
                        .ToListAsync();
    }

    public async Task<List<GolfClub>> GetMyBagAsync()
    {
        await Init();
        return await _db.Table<GolfClub>().ToListAsync();
    }

    public async Task SaveShotAsync(int clubId, double distance)
    {
        await Init();

        var shot = new TrackedShot { ClubId = clubId, Distance = distance, DateHit = DateTime.Now };
        await _db.InsertAsync(shot);

        var club = await _db.Table<GolfClub>().Where(c => c.Id == clubId).FirstOrDefaultAsync();
        if (club != null)
        {
            club.ShotCount += 1;
            club.TotalYardage += distance;
            await _db.UpdateAsync(club);
        }
    }

    public async Task<List<AutoCaddie.Models.GolfRound>> GetAllRoundsAsync()
    {
        await Init();
        return await _db.Table<AutoCaddie.Models.GolfRound>()
                        .OrderByDescending(r => r.DatePlayed)
                        .ToListAsync();
    }

    public async Task<List<AutoCaddie.Models.HoleScore>> GetHoleScoresAsync(int roundId)
    {
        await Init();
        return await _db.Table<AutoCaddie.Models.HoleScore>()
                        .Where(s => s.RoundId == roundId)
                        .OrderBy(s => s.HoleNumber)
                        .ToListAsync();
    }

    // NEW: Pulls the saved tracer coordinates back out so the UI can draw the lines
    public async Task<List<AutoCaddie.Models.RoundShotLocation>> GetShotLocationsForRoundAsync(int roundId)
    {
        await Init();
        return await _db.Table<AutoCaddie.Models.RoundShotLocation>()
                        .Where(s => s.RoundId == roundId)
                        .OrderBy(s => s.HoleNumber)
                        .ThenBy(s => s.StrokeNumber)
                        .ToListAsync();
    }

    public async Task DeleteCourseAsync(string courseName)
    {
        await Init();

        var holes = await _db.Table<GolfHole>().Where(h => h.CourseName == courseName).ToListAsync();
        foreach (var hole in holes)
        {
            await _db.DeleteAsync(hole);
        }

        var features = await _db.Table<CourseMapFeature>().Where(f => f.CourseName == courseName).ToListAsync();
        foreach (var feature in features)
        {
            await _db.DeleteAsync(feature);
        }
    }

    public async Task<List<string>> GetAvailableTeesAsync(string courseName)
    {
        await Init();

        var tees = await _db.Table<AutoCaddie.Models.CourseMapFeature>()
                            .Where(f => f.CourseName == courseName && f.FeatureType == "Tee")
                            .ToListAsync();

        var distinctColors = tees.Select(t => t.TeeColor?.ToUpper())
                                 .Where(c => !string.IsNullOrEmpty(c))
                                 .Distinct()
                                 .ToList();

        return distinctColors;
    }
}