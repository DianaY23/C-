using System;
using System.Collections.Generic;
using System.Linq;

namespace PackingApp.Shared
{
    public class Rect
    {
        public int Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public Rect() { }

        public Rect(int id, int w, int h, int x = 0, int y = 0)
        {
            Id = id; Width = w; Height = h; X = x; Y = y;
        }

        public bool Intersects(Rect other)
        {
            return X < other.X + other.Width && X + Width > other.X &&
                   Y < other.Y + other.Height && Y + Height > other.Y;
        }

        public Rect Clone()
        {
            return new Rect(Id, Width, Height, X, Y);
        }
    }

    public class Individual
    {
        public List<Rect> Rectangles { get; set; } = new();
        public double Fitness { get; set; }
        public int BoundingBoxArea { get; set; }
        public int Overlaps { get; set; }
        public int EmptyCells { get; set; }

        public Individual() { }

        public Individual(List<Rect> rects)
        {
            Rectangles = rects;
            CalculateFitness();
        }

        public void CalculateFitness()
        {
            if (Rectangles == null || Rectangles.Count == 0)
            {
                Fitness = 0;
                BoundingBoxArea = 0;
                Overlaps = 0;
                EmptyCells = 0;
                return;
            }

            int minX = Rectangles.Min(r => r.X);
            int minY = Rectangles.Min(r => r.Y);
            int maxX = Rectangles.Max(r => r.X + r.Width);
            int maxY = Rectangles.Max(r => r.Y + r.Height);

            BoundingBoxArea = (maxX - minX) * (maxY - minY);
            int sumArea = Rectangles.Sum(r => r.Width * r.Height);
            EmptyCells = BoundingBoxArea - sumArea;

            int overlaps = 0;
            for (int i = 0; i < Rectangles.Count; i++)
            {
                for (int j = i + 1; j < Rectangles.Count; j++)
                {
                    if (Rectangles[i].Intersects(Rectangles[j]))
                    {
                        overlaps++;
                    }
                }
            }
            Overlaps = overlaps;

            if (Overlaps == 0)
                Fitness = BoundingBoxArea;
            else
                Fitness = 1_000_000_000.0 + Overlaps * 1_000_000.0 + BoundingBoxArea;
        }
    }

    public class ProgressReport
    {
        public Individual Best { get; set; } = null!;
        public int Generation { get; set; }
    }
}
