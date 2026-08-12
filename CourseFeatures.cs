using System.Text.Json;

namespace AutoCaddie.Models
{
    public class CourseMapFeature
    {
        // Primary Key for SQLite
        public int Id { get; set; }

        public string CourseName { get; set; }

        // 1-18, or 0 if it's a massive lake spanning the whole course
        public int HoleNumber { get; set; }

        // "HoleLine", "Green", "Tee", "Fairway", "Bunker", "Water"
        public string FeatureType { get; set; }

        // "white", "blue", "red" (Only used for Tees)
        public string TeeColor { get; set; }

        // The raw JSON string of the polygon nodes to save in SQLite
        public string SerializedCoordinates { get; set; }
    }
}