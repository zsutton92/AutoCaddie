using SQLite;

namespace AutoCaddie.Models;

public class GolfHole
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public string CourseName { get; set; }
    public int HoleNumber { get; set; }
    public int Par { get; set; }

    // Tee coordinates (Start of the fairway)
    public double TeeLatitude { get; set; }
    public double TeeLongitude { get; set; }

    // Green coordinates (End of the fairway)
    public double GreenLatitude { get; set; }
    public double GreenLongitude { get; set; }

    public bool IsFavorite { get; set; }
}