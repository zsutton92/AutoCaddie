using System;
using System.Collections.Generic;
using System.Text;
using SQLite;
using AutoCaddie.Models;

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
        return await _db.Table<AutoCaddie.Models.CourseMapFeature>()
                        .Where(f => f.CourseName == courseName &&
                              (f.HoleNumber == holeNumber || f.HoleNumber == 0))
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

    public async Task SaveCompletedRoundAsync(string courseName, List<GolfHole> courseHoles, Dictionary<int, int> roundScores)
    {
        await Init();

        int totalScore = 0;
        int totalPar = 0;

        // Calculate totals
        foreach (var hole in courseHoles)
        {
            totalPar += hole.Par;
            if (roundScores.ContainsKey(hole.HoleNumber))
            {
                totalScore += roundScores[hole.HoleNumber];
            }
        }

        // 1. Create and save the main Round record
        var newRound = new GolfRound
        {
            CourseName = courseName,
            DatePlayed = DateTime.Now,
            TotalScore = totalScore,
            TotalPar = totalPar
        };

        await _db.InsertAsync(newRound);

        // 2. Save each individual hole score linked to the Round ID
        foreach (var hole in courseHoles)
        {
            int strokes = roundScores.ContainsKey(hole.HoleNumber) ? roundScores[hole.HoleNumber] : hole.Par; // Default to par if skipped

            var holeScore = new HoleScore
            {
                RoundId = newRound.Id,
                HoleNumber = hole.HoleNumber,
                Par = hole.Par,
                Strokes = strokes
            };

            await _db.InsertAsync(holeScore);
        }
    }

    // Fetches all completed rounds, sorted by newest first
    public async Task<List<GolfRound>> GetAllRoundsAsync()
    {
        await Init();
        return await _db.Table<GolfRound>().OrderByDescending(r => r.DatePlayed).ToListAsync();
    }

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
}
