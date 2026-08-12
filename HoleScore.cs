using SQLite;

namespace AutoCaddie.Models;

public class HoleScore
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed] // Links this score to a specific GolfRound
    public int RoundId { get; set; }

    public int HoleNumber { get; set; }
    public int Par { get; set; }
    public int Strokes { get; set; }
}