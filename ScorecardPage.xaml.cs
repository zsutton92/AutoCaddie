using Microcharts;
using SkiaSharp;
using AutoCaddie.Models;

namespace AutoCaddie
{
    public partial class ScorecardPage : ContentPage
    {
        public ScorecardPage(GolfRound currentRound, List<HoleScore> roundScores)
        {
            InitializeComponent();

            // 1. Populate the Header Labels
            CourseNameLabel.Text = currentRound.CourseName;
            DateLabel.Text = currentRound.DatePlayed.ToString("MMMM dd, yyyy");
            TotalScoreLabel.Text = currentRound.TotalScore.ToString();

            GirLabel.Text = currentRound.TotalGIR.ToString();
            FirLabel.Text = currentRound.TotalFIR.ToString();

            int totalPar = roundScores.Sum(h => h.Par);
            int toPar = currentRound.TotalScore - totalPar;
            ToParLabel.Text = toPar == 0 ? "E" : (toPar > 0 ? $"+{toPar}" : toPar.ToString());

            // 2. Populate the Horizontal Scorecard Grids
            var frontHoles = roundScores.Where(h => h.HoleNumber <= 9).OrderBy(h => h.HoleNumber).ToList();
            var backHoles = roundScores.Where(h => h.HoleNumber > 9 && h.HoleNumber <= 18).OrderBy(h => h.HoleNumber).ToList();

            PopulateScorecardHalf(Front9Grid, frontHoles, "OUT");
            PopulateScorecardHalf(Back9Grid, backHoles, "IN");

            // 3. Render the Microcharts
            RenderCharts(roundScores);
        }

        private void PopulateScorecardHalf(Grid targetGrid, List<HoleScore> holes, string totalLabel)
        {
            if (holes == null || holes.Count == 0) return;

            // Columns: Hole 1..9 (or 10..18) + OUT/IN summary column
            for (int i = 0; i <= holes.Count; i++)
            {
                targetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
            }

            // Rows: Header (Hole #), Par, Score, Putts
            targetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Hole
            targetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Par
            targetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Score
            targetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Putts

            int totalHalfPar = 0;
            int totalHalfScore = 0;
            int totalHalfPutts = 0;

            // Get current theme to ensure text is visible in Light/Dark mode
            bool isDarkTheme = Application.Current.RequestedTheme == AppTheme.Dark;
            Color primaryTextColor = isDarkTheme ? Colors.White : Color.FromArgb("#222222");
            Color parTextColor = isDarkTheme ? Color.FromArgb("#AAAAAA") : Color.FromArgb("#888888");

            for (int col = 0; col < holes.Count; col++)
            {
                var hole = holes[col];
                totalHalfPar += hole.Par;

                bool hasScore = hole.Strokes > 0;
                bool hasPutts = hole.Putts > 0;

                // 1. Hole Header Label
                targetGrid.Add(new Label
                {
                    Text = hole.HoleNumber.ToString(),
                    FontSize = 11,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Color.FromArgb("#7A68E6"),
                    HorizontalTextAlignment = TextAlignment.Center
                }, col, 0);

                // 2. Par Label
                targetGrid.Add(new Label
                {
                    Text = hole.Par.ToString(),
                    FontSize = 12,
                    TextColor = parTextColor,
                    HorizontalTextAlignment = TextAlignment.Center
                }, col, 1);

                // 3. Score Element (Traditional Circles and Squares)
                View scoreElement;

                if (hasScore)
                {
                    totalHalfScore += hole.Strokes;
                    int diff = hole.Strokes - hole.Par;

                    var cellGrid = new Grid
                    {
                        WidthRequest = 28,
                        HeightRequest = 28,
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center
                    };

                    Color shapeColor = Colors.Transparent;

                    if (diff < 0) shapeColor = Color.FromArgb("#4CAF50"); // Green for Birdie/Under
                    else if (diff > 0) shapeColor = Color.FromArgb("#F44336"); // Red for Bogey/Over

                    double thickness = 1.5;

                    // Under Par (Circles)
                    if (diff < 0)
                    {
                        int circles = Math.Abs(diff);
                        if (circles > 3) circles = 3;

                        for (int i = 0; i < circles; i++)
                        {
                            cellGrid.Children.Add(new Microsoft.Maui.Controls.Shapes.Ellipse
                            {
                                Stroke = shapeColor,
                                StrokeThickness = thickness,
                                Margin = new Thickness(i * 3),
                                Fill = Colors.Transparent
                            });
                        }
                    }
                    // Over Par (Squares)
                    else if (diff > 0)
                    {
                        int squares = diff;
                        if (squares > 3) squares = 3;

                        for (int i = 0; i < squares; i++)
                        {
                            cellGrid.Children.Add(new Microsoft.Maui.Controls.Shapes.Rectangle
                            {
                                Stroke = shapeColor,
                                StrokeThickness = thickness,
                                Margin = new Thickness(i * 3),
                                Fill = Colors.Transparent
                            });
                        }
                    }

                    // Add the actual score number
                    cellGrid.Children.Add(new Label
                    {
                        Text = hole.Strokes.ToString(),
                        FontSize = 13,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = primaryTextColor,
                        HorizontalTextAlignment = TextAlignment.Center,
                        VerticalTextAlignment = TextAlignment.Center
                    });

                    scoreElement = cellGrid;
                }
                else
                {
                    // Fallback if no score was recorded for this hole
                    scoreElement = new Label
                    {
                        Text = "-",
                        FontSize = 13,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = parTextColor,
                        HorizontalTextAlignment = TextAlignment.Center,
                        VerticalTextAlignment = TextAlignment.Center
                    };
                }

                targetGrid.Add(scoreElement, col, 2);

                // 4. Putts Label
                if (hasPutts) totalHalfPutts += hole.Putts;

                targetGrid.Add(new Label
                {
                    Text = hasPutts ? hole.Putts.ToString() : "-",
                    FontSize = 11,
                    TextColor = parTextColor,
                    HorizontalTextAlignment = TextAlignment.Center
                }, col, 3);
            }

            // --- Totals Column (OUT / IN) ---
            int totalCol = holes.Count;

            targetGrid.Add(new Label
            {
                Text = totalLabel,
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#4A34D4"),
                HorizontalTextAlignment = TextAlignment.Center
            }, totalCol, 0);

            targetGrid.Add(new Label
            {
                Text = totalHalfPar.ToString(),
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                TextColor = primaryTextColor,
                HorizontalTextAlignment = TextAlignment.Center
            }, totalCol, 1);

            targetGrid.Add(new Label
            {
                Text = totalHalfScore > 0 ? totalHalfScore.ToString() : "-",
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#4CAF50"),
                HorizontalTextAlignment = TextAlignment.Center
            }, totalCol, 2);

            targetGrid.Add(new Label
            {
                Text = totalHalfPutts > 0 ? totalHalfPutts.ToString() : "-",
                FontSize = 11,
                TextColor = parTextColor,
                HorizontalTextAlignment = TextAlignment.Center
            }, totalCol, 3);
        }

        private void RenderCharts(List<HoleScore> holeScores)
        {
            var strokeEntries = new List<ChartEntry>();
            var puttEntries = new List<ChartEntry>();

            foreach (var hole in holeScores.OrderBy(h => h.HoleNumber))
            {
                int relativeToPar = hole.Strokes - hole.Par;

                SKColor strokeColor = relativeToPar < 0 ? SKColor.Parse("#4CAF50") : (relativeToPar > 0 ? SKColor.Parse("#F44336") : SKColor.Parse("#9E9E9E"));

                strokeEntries.Add(new ChartEntry(hole.Strokes)
                {
                    Label = hole.HoleNumber.ToString(),
                    ValueLabel = hole.Strokes.ToString(),
                    Color = strokeColor,
                    ValueLabelColor = strokeColor
                });

                SKColor puttColor = hole.Putts >= 3 ? SKColor.Parse("#FF9800") : SKColor.Parse("#4CAF50");

                puttEntries.Add(new ChartEntry(hole.Putts)
                {
                    Label = hole.HoleNumber.ToString(),
                    ValueLabel = hole.Putts.ToString(),
                    Color = puttColor,
                    ValueLabelColor = puttColor
                });
            }

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