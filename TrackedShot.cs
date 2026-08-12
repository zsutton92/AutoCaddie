using SQLite;

namespace AutoCaddie.Models;

public class TrackedShot
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    public int ClubId { get; set; }
    public double Distance { get; set; }
    public DateTime DateHit { get; set; }
}