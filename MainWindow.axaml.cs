using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ScottPlot.Avalonia;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PackingApp.Shared;

namespace PackingApp
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource? _cts;
        private readonly List<double> _fitnessHistory = new List<double>();
        private readonly List<int> _generationHistory = new List<int>();
        private int _currentGeneration = 0;
        private const double CellSize = 20;

        public MainWindow()
        {
            InitializeComponent();
            InitPlot();
        }

        private void InitPlot()
        {
            FitnessPlot.Plot.Clear();
            FitnessPlot.Plot.Title("История Fitness");
            FitnessPlot.Plot.XLabel("Поколение");
            FitnessPlot.Plot.YLabel("Fitness");
            FitnessPlot.Plot.Axes.SetLimitsX(0, 100);
            FitnessPlot.Plot.Axes.SetLimitsY(0, 100);
            FitnessPlot.Refresh();
        }

        private async void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            int count = (int)(CountInput.Value ?? 10);
            if (count <= 0) { BestAreaText.Text = "Ошибка: N должно быть > 0"; return; }

            StartBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;
            _fitnessHistory.Clear();
            _generationHistory.Clear();
            _currentGeneration = 0;
            InitPlot();

            var shapes = new List<PackingApp.Shared.Rect>();
            var rand = new Random();
            for (int i = 0; i < count; i++)
            {
                int type = rand.Next(1, 4);
                shapes.Add(new PackingApp.Shared.Rect(i, type, type));
            }

            string inputText = ServicesInput.Text ?? "http://localhost:5267";
            var services = inputText
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToList();

            if (services.Count == 0) services.Add("http://localhost:5267");

            _cts = new CancellationTokenSource();

            var tasks = services.Select(url => RunOnService(url, shapes, _cts.Token)).ToList();

            try
            {
                await Task.WhenAny(tasks);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка: {ex.Message}");
                Dispatcher.UIThread.Post(() => BestAreaText.Text = $"Ошибка: {ex.Message}");
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    StartBtn.IsEnabled = true;
                    StopBtn.IsEnabled = false;
                });
            }
        }

        private async Task RunOnService(string baseUrl, List<PackingApp.Shared.Rect> shapes, CancellationToken token)
        {
            try
            {
                int popSize = 100;
                int gens = 500;
                
                var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/packing/solve?popSize={popSize}&gens={gens}");
                request.Content = JsonContent.Create(shapes);

                using var response = await new HttpClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(token);
                using var reader = new System.IO.StreamReader(stream);

                while (!reader.EndOfStream && !token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line != null && line.StartsWith("data: "))
                    {
                        var json = line.Substring(6);
                        var report = JsonSerializer.Deserialize<ProgressReport>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (report != null)
                        {
                            Dispatcher.UIThread.Post(() => UpdateUI(report));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка сервиса {baseUrl}: {ex.Message}");
            }
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private void UpdateUI(ProgressReport report)
        {
            var best = report.Best;
            _currentGeneration = report.Generation;

            string overlapsStr = best.Overlaps == 0 ? "пересечений: 0" : $" {best.Overlaps}";
            BestAreaText.Text = $"Площадь: {best.BoundingBoxArea} ({overlapsStr}, пустых: {best.EmptyCells})";
            
            DrawSolution(best);

            _fitnessHistory.Add(best.Fitness);
            _generationHistory.Add(_currentGeneration);

            if (_fitnessHistory.Count > 100)
            {
                _fitnessHistory.RemoveAt(0);
                _generationHistory.RemoveAt(0);
            }

            UpdatePlot();
        }

        private void UpdatePlot()
        {
            try
            {
                FitnessPlot.Plot.Clear();
                if (_fitnessHistory.Count > 0)
                {
                    double[] xs = _generationHistory.Select(g => (double)g).ToArray();
                    double[] ys = _fitnessHistory.ToArray();

                    var scatter = FitnessPlot.Plot.Add.Scatter(xs, ys);
                    scatter.LineWidth = 2;
                    scatter.Color = ScottPlot.Color.FromHex("#0500a3");
                    scatter.MarkerSize = 4;

                    double minY = ys.Min();
                    double maxY = ys.Max();
                    double padding = Math.Max(1, (maxY - minY) * 0.1);
                    FitnessPlot.Plot.Axes.SetLimitsY(minY - padding, maxY + padding);

                    double minX = xs.Min();
                    double maxX = Math.Max(xs.Max(), minX + 10);
                    FitnessPlot.Plot.Axes.SetLimitsX(minX, maxX + 2);
                }
                FitnessPlot.Refresh();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка построения {ex.Message}");
            }
        }

        private void DrawSolution(Individual ind)
        {
            DrawingCanvas.Children.Clear();
            if (ind.Rectangles == null || !ind.Rectangles.Any()) return;

            int minX = ind.Rectangles.Min(r => r.X);
            int minY = ind.Rectangles.Min(r => r.Y);

            var colors = new List<IBrush>
            {
                Brushes.Orange, Brushes.LightGreen, Brushes.LightBlue,
                Brushes.Pink, Brushes.Plum, Brushes.LightYellow, Brushes.LightCoral
            };

            foreach (var rect in ind.Rectangles)
            {
                var border = new Border
                {
                    Width = rect.Width * CellSize,
                    Height = rect.Height * CellSize,
                    Background = colors[rect.Id % colors.Count],
                    BorderBrush = Brushes.Black,
                    BorderThickness = new Thickness(1)
                };
                Canvas.SetLeft(border, (rect.X - minX) * CellSize);
                Canvas.SetTop(border, (rect.Y - minY) * CellSize);
                DrawingCanvas.Children.Add(border);
            }
        }
    }
}
