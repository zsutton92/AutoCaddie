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

    //public async Task SaveHoleAsync(GolfHole hole)
    //{
    //    await Init();
    //    await _db.InsertAsync(hole);
    //}

    public async Task SaveMapFeatureAsync(AutoCaddie.Models.CourseMapFeature feature)
    {
        await Init();
        await _db.InsertAsync(feature);
    }

    // Fetches every feature for the entire course (useful for deleting or exporting)
    public async Task<List<AutoCaddie.Models.CourseMapFeature>> GetMapFeaturesForCourseAsync(string courseName)
    {
        await Init();
        return await _db.Table<AutoCaddie.Models.CourseMapFeature>()
                        .Where(f => f.CourseName == courseName)
                        .ToListAsync();
    }

    // Fetches just the features for the specific hole you are playing right now
    public async Task<List<AutoCaddie.Models.CourseMapFeature>> GetMapFeaturesForHoleAsync(string courseName, int holeNumber)
    {
        await Init();

        // We also pull features where HoleNumber == 0 in case we saved a giant 
        // lake or river that applies to the whole course!
        //return await _db.Table<AutoCaddie.Models.CourseMapFeature>()
        //                .Where(f => f.CourseName == courseName &&
        //                      (f.HoleNumber == holeNumber || f.HoleNumber == 0))
        //                .ToListAsync();
        return await _db.Table<AutoCaddie.Models.CourseMapFeature>()
                    .Where(f => f.CourseName == courseName &&
                          (f.HoleNumber == holeNumber ||
                          (f.HoleNumber == 0 && f.FeatureType != "Tee" && f.FeatureType != "Green")))
                    .ToListAsync();
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
        // Note: If your SQLite connection variable is named something other than _db 
        // (like _database or _connection), update it here!
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


    public async Task SaveCompletedRoundAsync(string courseName, List<AutoCaddie.Models.GolfHole> holes, Dictionary<int, int> scores, Dictionary<int, int> putts, Dictionary<int, bool?> fairways)
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
    }

    // Fetches all completed rounds, sorted by newest first
    //public async Task<List<GolfRound>> GetAllRoundsAsync()
    //{
    //    await Init();
    //    return await _db.Table<GolfRound>().OrderByDescending(r => r.DatePlayed).ToListAsync();
    //}

    // Fetches the specific hole-by-hole scores for a single round
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

        // 1. Log the individual shot
        var shot = new TrackedShot { ClubId = clubId, Distance = distance, DateHit = DateTime.Now };
        await _db.InsertAsync(shot);

        // 2. Update the club's running averages
        var club = await _db.Table<GolfClub>().Where(c => c.Id == clubId).FirstOrDefaultAsync();
        if (club != null)
        {
            club.ShotCount += 1;
            club.TotalYardage += distance;
            await _db.UpdateAsync(club);
        }
    }

    // Gets the high-level summary of all your rounds
    public async Task<List<AutoCaddie.Models.GolfRound>> GetAllRoundsAsync()
    {
        await Init();
        // Order by newest rounds first
        return await _db.Table<AutoCaddie.Models.GolfRound>()
                        .OrderByDescending(r => r.DatePlayed)
                        .ToListAsync();
    }

    // Gets the 18-hole breakdown for a specific round
    public async Task<List<AutoCaddie.Models.HoleScore>> GetHoleScoresAsync(int roundId)
    {
        await Init();
        return await _db.Table<AutoCaddie.Models.HoleScore>()
                        .Where(s => s.RoundId == roundId)
                        .OrderBy(s => s.HoleNumber)
                        .ToListAsync();
    }

    public async Task DeleteCourseAsync(string courseName)
    {
        await Init();

        // 1. Delete all holes for this course
        var holes = await _db.Table<GolfHole>().Where(h => h.CourseName == courseName).ToListAsync();
        foreach (var hole in holes)
        {
            await _db.DeleteAsync(hole);
        }

        // 2. Delete all map features (fairways, greens, etc.) for this course
        var features = await _db.Table<CourseMapFeature>().Where(f => f.CourseName == courseName).ToListAsync();
        foreach (var feature in features)
        {
            await _db.DeleteAsync(feature);
        }
    }

    public async Task<List<string>> GetAvailableTeesAsync(string courseName)
    {
        await Init(); // Ensure DB is initialized

        // Grab all features for this course that are tagged as "Tee"
        var tees = await _db.Table<AutoCaddie.Models.CourseMapFeature>()
                            .Where(f => f.CourseName == courseName && f.FeatureType == "Tee")
                            .ToListAsync();

        // Extract the colors, remove duplicates, and capitalize them nicely
        var distinctColors = tees.Select(t => t.TeeColor?.ToUpper())
                                 .Where(c => !string.IsNullOrEmpty(c))
                                 .Distinct()
                                 .ToList();

        return distinctColors;
    }



}
