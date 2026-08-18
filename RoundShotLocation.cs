using SQLite;

namespace AutoCaddie.Models;

public class RoundShotLocation
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public int RoundId { get; set; }

    public int HoleNumber { get; set; }
    public int StrokeNumber { get; set; } // Helps connect the dots in the right order

    public double Latitude { get; set; }
    public double Longitude { get; set; }
}