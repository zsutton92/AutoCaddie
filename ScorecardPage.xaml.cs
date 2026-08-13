using Microcharts;
using SkiaSharp;
using AutoCaddie.Models; // Make sure your models are referenced

namespace AutoCaddie
{
    public partial class ScorecardPage : ContentPage
    {
        // UPDATED: Pass the Round info AND the Hole scores
        public ScorecardPage(GolfRound currentRound, List<HoleScore> roundScores)
        {
            InitializeComponent();

            // 1. Populate the Header Labels
            CourseNameLabel.Text = currentRound.CourseName;
            DateLabel.Text = currentRound.DatePlayed.ToString("MMMM dd, yyyy");
            TotalScoreLabel.Text = currentRound.TotalScore.ToString();

            // NEW: Populate the GIR and FIR stats
            GirLabel.Text = currentRound.TotalGIR.ToString();
            FirLabel.Text = currentRound.TotalFIR.ToString();

            // Calculate To Par (Assuming a standard par 72 if we don't have the course total par handy, 
            // or we can sum it up from the holeScores)
            int totalPar = roundScores.Sum(h => h.Par);
            int toPar = currentRound.TotalScore - totalPar;
            ToParLabel.Text = toPar == 0 ? "E" : (toPar > 0 ? $"+{toPar}" : toPar.ToString());

            // 2. Populate the 18-Hole Grid
            // Since we used a VerticalStackLayout with BindableLayout in the XAML
            BindableLayout.SetItemsSource(ScoresList, roundScores.OrderBy(h => h.HoleNumber));

            // 3. Render the Microcharts
            RenderCharts(roundScores);
        }

        private void RenderCharts(List<HoleScore> holeScores)
        {
            var strokeEntries = new List<ChartEntry>();
            var puttEntries = new List<ChartEntry>();

            // Ensure the data is in chronological order from Hole 1 to 18
            foreach (var hole in holeScores.OrderBy(h => h.HoleNumber))
            {
                // --- 1. STROKE TREND DATA ---
                int relativeToPar = hole.Strokes - hole.Par;

                // Color Code: Blue (Under Par), Gray (Par), Red (Over Par)
                SKColor strokeColor = relativeToPar < 0 ? SKColors.Blue : (relativeToPar > 0 ? SKColors.Red : SKColors.Gray);

                strokeEntries.Add(new ChartEntry(hole.Strokes)
                {
                    Label = hole.HoleNumber.ToString(), // X-Axis (Hole 1, 2, etc.)
                    ValueLabel = hole.Strokes.ToString(), // Number above the point
                    Color = strokeColor,
                    ValueLabelColor = strokeColor
                });

                // --- 2. PUTTS DATA ---
                // Color Code: Green (1-2 putts), Orange (3+ putts)
                SKColor puttColor = hole.Putts >= 3 ? SKColors.Orange : SKColors.ForestGreen;

                puttEntries.Add(new ChartEntry(hole.Putts)
                {
                    Label = hole.HoleNumber.ToString(),
                    ValueLabel = hole.Putts.ToString(),
                    Color = puttColor,
                    ValueLabelColor = puttColor
                });
            }

            // --- 3. RENDER LINE CHART ---
            StrokeTrendChart.Chart = new LineChart
            {
                Entries = strokeEntries,
                LineMode = LineMode.Straight,
                PointMode = PointMode.Circle,
                PointSize = 12,
                LineSize = 4,
                BackgroundColor = SKColors.Transparent,
                LabelTextSize = 26,
                ValueLabelOrientation = Orientation.Horizontal
            };

            // --- 4. RENDER BAR CHART ---
            PuttsChart.Chart = new BarChart
            {
                Entries = puttEntries,
                BackgroundColor = SKColors.Transparent,
                LabelTextSize = 26,
                ValueLabelOrientation = Orientation.Horizontal
            };
        }
    }
}