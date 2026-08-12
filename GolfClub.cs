using SQLite;

namespace AutoCaddie.Models;

public class GolfClub
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    public string Name { get; set; }
    public double TotalYardage { get; set; } // Sum of all shots hit
    public int ShotCount { get; set; }       // Number of times used

    // Helper property to instantly get the average
    [Ignore]
    public double AverageYardage => ShotCount > 0 ? TotalYardage / ShotCount : 0;
}