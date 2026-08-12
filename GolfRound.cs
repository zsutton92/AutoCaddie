using SQLite;

namespace AutoCaddie.Models;

public class GolfRound
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    public string CourseName { get; set; }
    public DateTime DatePlayed { get; set; }
    public int TotalScore { get; set; }
    public int TotalPar { get; set; }
}