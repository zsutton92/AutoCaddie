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
    public int Putts { get; set; }

    public bool HitGIR
    {
        get
        {
            // GIR means you are on the green with 2 putts left for par.
            // Par 3: On in 1. Par 4: On in 2. Par 5: On in 3.
            return (Strokes - Putts) <= (Par - 2);
        }
    }

    public bool? HitFairway { get; set; }
}