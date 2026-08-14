namespace AutoCaddie.Models;

public class CourseWeather
{
    public double Temperature { get; set; }
    public double WindSpeedMph { get; set; }
    public double WindDirectionDegrees { get; set; }
    public string Condition { get; set; } = "Clear";
    public DateTime LastUpdated { get; set; }
}