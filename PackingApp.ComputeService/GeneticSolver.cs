using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PackingApp.Shared;

namespace PackingApp.ComputeService
{
    public class GeneticSolver
    {
        private readonly int _populationSize;
        private readonly int _generations;
        private readonly List<Rect> _baseShapes;
        private readonly int _gridSize;
        private const int TournamentSize = 4;
        private const double MutationProbability = 0.35;
        private const double StrongMutationProbability = 0.10;
        private const double RandomPlacementProbability = 0.15;
        private const int LocalSearchPasses = 3;
        private const int ElitePercent = 10;
        private const int MaxNoImprovement = 120;

        private readonly ThreadLocal<Random> _random =
            new ThreadLocal<Random>(() => new Random(Guid.NewGuid().GetHashCode()));

        public GeneticSolver(List<Rect> shapes, int popSize = 100, int gens = 500)
        {
            _baseShapes = shapes.Select(s => s.Clone()).ToList();
            _populationSize = Math.Max(10, popSize);
            _generations = Math.Max(1, gens);

            int totalArea = _baseShapes.Sum(s => s.Width * s.Height);
            int maxSide = _baseShapes.Count == 0 ? 1 : _baseShapes.Max(s => Math.Max(s.Width, s.Height));
            _gridSize = Math.Max(20, (int)Math.Ceiling(Math.Sqrt(totalArea)) * 4 + maxSide);
        }

        public async Task<Individual> SolveAsync(IProgress<ProgressReport> progress, CancellationToken token)
        {
            var population = new List<Individual>(_populationSize);

            for (int i = 0; i < _populationSize; i++)
            {
                token.ThrowIfCancellationRequested();
                Individual individual = CreateRandomIndividual();
                PackIndividual(individual, allowRandomChoices: true);
                population.Add(individual);
            }

            Parallel.ForEach(population, individual => individual.CalculateFitness());
            population = population.OrderBy(i => i.Fitness).ToList();

            Individual bestOverall = CloneIndividual(population[0]);
            int noImprovement = 0;

            progress?.Report(new ProgressReport { Best = CloneIndividual(bestOverall), Generation = 0 });

            for (int generation = 1; generation <= _generations; generation++)
            {
                if (token.IsCancellationRequested) break;

                Parallel.ForEach(population, individual => individual.CalculateFitness());
                population = population.OrderBy(i => i.Fitness).ToList();

                Individual currentBest = population[0];

                if (currentBest.Fitness < bestOverall.Fitness)
                {
                    bestOverall = CloneIndividual(currentBest);
                    noImprovement = 0;
                }
                else
                {
                    noImprovement++;
                }

                if (generation % 2 == 0 || generation == _generations)
                {
                    progress?.Report(new ProgressReport { Best = CloneIndividual(bestOverall), Generation = generation });
                }

                if (noImprovement >= MaxNoImprovement) break;

                var newPopulation = new List<Individual>(_populationSize);
                int eliteCount = Math.Max(2, _populationSize * ElitePercent / 100);
                eliteCount = Math.Min(eliteCount, population.Count);

                for (int i = 0; i < eliteCount; i++)
                {
                    newPopulation.Add(CloneIndividual(population[i]));
                }

                int childrenCount = _populationSize - newPopulation.Count;
                var children = new Individual[childrenCount];

                Parallel.For(0, childrenCount, new ParallelOptions { CancellationToken = token }, i =>
                {
                    Random random = _random.Value!;
                    Individual parent1 = TournamentSelect(population, random);
                    Individual parent2 = TournamentSelect(population, random);
                    Individual child = CrossoverOrderBased(parent1, parent2, random);
                    Mutate(child, random);
                    PackIndividual(child, allowRandomChoices: true);
                    children[i] = child;
                });

                newPopulation.AddRange(children);
                population = newPopulation;
                await Task.Yield();
            }

            if (!token.IsCancellationRequested)
            {
                LocalSearch(bestOverall, token);
                bestOverall.CalculateFitness();
            }

            progress?.Report(new ProgressReport { Best = CloneIndividual(bestOverall), Generation = _generations });
            return bestOverall;
        }

        private Individual CreateRandomIndividual()
        {
            var rectangles = _baseShapes.Select(s => new Rect(s.Id, s.Width, s.Height)).ToList();
            Shuffle(rectangles, _random.Value!);
            return new Individual(rectangles);
        }

        private void PackIndividual(Individual individual, bool allowRandomChoices)
        {
            if (individual.Rectangles.Count == 0) return;

            foreach (Rect rect in individual.Rectangles) { rect.X = 0; rect.Y = 0; }

            var placed = new List<Rect>(individual.Rectangles.Count);
            Random random = _random.Value!;

            foreach (Rect rect in individual.Rectangles)
            {
                if (placed.Count == 0)
                {
                    rect.X = 0; rect.Y = 0;
                    placed.Add(rect);
                    continue;
                }

                Position bestPosition = FindBestPosition(rect, placed, random, allowRandomChoices);
                rect.X = bestPosition.X;
                rect.Y = bestPosition.Y;
                placed.Add(rect);
            }

            CompactDownAndLeft(individual);
            individual.CalculateFitness();
        }

        private Position FindBestPosition(Rect rect, List<Rect> placed, Random random, bool allowRandomChoices)
        {
            var xCandidates = new HashSet<int>();
            var yCandidates = new HashSet<int>();
            xCandidates.Add(0); yCandidates.Add(0);

            foreach (Rect other in placed)
            {
                xCandidates.Add(other.X + other.Width);
                yCandidates.Add(other.Y + other.Height);
            }

            var positions = new List<Position>();

            foreach (int x in xCandidates)
            {
                foreach (int y in yCandidates)
                {
                    if (x < 0 || y < 0) continue;
                    if (x + rect.Width > _gridSize) continue;
                    if (y + rect.Height > _gridSize) continue;

                    Rect candidate = new Rect(rect.Id, rect.Width, rect.Height, x, y);
                    bool collision = false;

                    foreach (Rect other in placed)
                    {
                        if (candidate.Intersects(other)) { collision = true; break; }
                    }

                    if (collision) continue;
                    int area = CalculateBoundingArea(placed, candidate);
                    positions.Add(new Position(x, y, area));
                }
            }

            if (positions.Count == 0) return FindFallbackPosition(rect, placed);

            positions = positions.OrderBy(p => p.Area).ThenBy(p => p.Y).ThenBy(p => p.X).ToList();

            if (allowRandomChoices && random.NextDouble() < RandomPlacementProbability && positions.Count > 1)
            {
                int count = Math.Min(5, positions.Count);
                int index = random.Next(count);
                return positions[index];
            }

            return positions[0];
        }

        private int CalculateBoundingArea(List<Rect> placed, Rect candidate)
        {
            int minX = candidate.X; int minY = candidate.Y;
            int maxX = candidate.X + candidate.Width; int maxY = candidate.Y + candidate.Height;

            foreach (Rect rect in placed)
            {
                minX = Math.Min(minX, rect.X); minY = Math.Min(minY, rect.Y);
                maxX = Math.Max(maxX, rect.X + rect.Width); maxY = Math.Max(maxY, rect.Y + rect.Height);
            }
            return (maxX - minX) * (maxY - minY);
        }

        private Position FindFallbackPosition(Rect rect, List<Rect> placed)
        {
            for (int y = 0; y <= _gridSize - rect.Height; y++)
            {
                for (int x = 0; x <= _gridSize - rect.Width; x++)
                {
                    Rect candidate = new Rect(rect.Id, rect.Width, rect.Height, x, y);
                    bool collision = false;

                    foreach (Rect other in placed)
                    {
                        if (candidate.Intersects(other)) { collision = true; break; }
                    }

                    if (!collision)
                    {
                        int area = CalculateBoundingArea(placed, candidate);
                        return new Position(x, y, area);
                    }
                }
            }
            return new Position(0, 0, int.MaxValue);
        }

        private void CompactDownAndLeft(Individual individual)
        {
            if (individual.Rectangles.Count == 0) return;
            bool changed = true;
            int iteration = 0;

            while (changed && iteration < 30)
            {
                changed = false; iteration++;

                foreach (Rect rect in individual.Rectangles)
                {
                    while (rect.Y > 0)
                    {
                        rect.Y--;
                        if (CollidesWithAny(rect, individual.Rectangles)) { rect.Y++; break; }
                        changed = true;
                    }
                }

                foreach (Rect rect in individual.Rectangles)
                {
                    while (rect.X > 0)
                    {
                        rect.X--;
                        if (CollidesWithAny(rect, individual.Rectangles)) { rect.X++; break; }
                        changed = true;
                    }
                }
            }

            int minX = individual.Rectangles.Min(r => r.X);
            int minY = individual.Rectangles.Min(r => r.Y);
            foreach (Rect rect in individual.Rectangles) { rect.X -= minX; rect.Y -= minY; }
        }

        private bool CollidesWithAny(Rect self, List<Rect> all)
        {
            foreach (Rect other in all)
            {
                if (ReferenceEquals(self, other)) continue;
                if (self.Intersects(other)) return true;
            }
            return false;
        }

        private Individual TournamentSelect(List<Individual> population, Random random)
        {
            Individual? best = null;
            for (int i = 0; i < TournamentSize; i++)
            {
                Individual candidate = population[random.Next(population.Count)];
                if (best == null || candidate.Fitness < best.Fitness) best = candidate;
            }
            return best!;
        }

        private Individual CrossoverOrderBased(Individual parent1, Individual parent2, Random random)
        {
            int n = parent1.Rectangles.Count;
            if (n == 0) return new Individual(new List<Rect>());
            if (n == 1) return new Individual(new List<Rect> { parent1.Rectangles[0].Clone() });

            int left = random.Next(n); int right = random.Next(n);
            if (left > right) { int temp = left; left = right; right = temp; }

            var childOrder = new List<int>(Enumerable.Repeat(-1, n));
            var used = new HashSet<int>();

            for (int i = left; i <= right; i++)
            {
                int id = parent1.Rectangles[i].Id;
                childOrder[i] = id;
                used.Add(id);
            }

            int childIndex = (right + 1) % n;
            int parentIndex = (right + 1) % n;
            int added = 0;

            while (added < n - (right - left + 1))
            {
                int id = parent2.Rectangles[parentIndex].Id;
                if (!used.Contains(id))
                {
                    childOrder[childIndex] = id;
                    used.Add(id);
                    childIndex = (childIndex + 1) % n;
                    added++;
                }
                parentIndex = (parentIndex + 1) % n;
            }

            var shapes = _baseShapes.ToDictionary(s => s.Id);
            var childRects = new List<Rect>(n);
            foreach (int id in childOrder)
            {
                Rect shape = shapes[id];
                childRects.Add(new Rect(shape.Id, shape.Width, shape.Height));
            }
            return new Individual(childRects);
        }

        private void Mutate(Individual individual, Random random)
        {
            if (individual.Rectangles.Count < 2) return;

            if (random.NextDouble() < MutationProbability)
            {
                int i = random.Next(individual.Rectangles.Count);
                int j = random.Next(individual.Rectangles.Count);
                Rect temp = individual.Rectangles[i];
                individual.Rectangles[i] = individual.Rectangles[j];
                individual.Rectangles[j] = temp;
            }

            if (random.NextDouble() < StrongMutationProbability)
            {
                int left = random.Next(individual.Rectangles.Count);
                int right = random.Next(individual.Rectangles.Count);
                if (left > right) { int temp = left; left = right; right = temp; }
                individual.Rectangles.Reverse(left, right - left + 1);
            }

            if (random.NextDouble() < 0.15)
            {
                int from = random.Next(individual.Rectangles.Count);
                int to = random.Next(individual.Rectangles.Count);
                if (from != to)
                {
                    Rect selected = individual.Rectangles[from];
                    individual.Rectangles.RemoveAt(from);
                    if (to > individual.Rectangles.Count) to = individual.Rectangles.Count;
                    individual.Rectangles.Insert(to, selected);
                }
            }
        }

        private void LocalSearch(Individual individual, CancellationToken token)
        {
            if (individual.Rectangles.Count < 2) return;
            individual.CalculateFitness();

            for (int pass = 0; pass < LocalSearchPasses; pass++)
            {
                if (token.IsCancellationRequested) return;
                bool improved = false;

                for (int i = 0; i < individual.Rectangles.Count; i++)
                {
                    if (token.IsCancellationRequested) return;
                    Rect current = individual.Rectangles[i];
                    int oldX = current.X; int oldY = current.Y;
                    int oldArea = individual.BoundingBoxArea;

                    var placed = individual.Rectangles.Where((r, index) => index != i).ToList();
                    if (placed.Count == 0) continue;

                    Position best = FindBestPosition(current, placed, _random.Value!, false);

                    if (best.Area < oldArea)
                    {
                        current.X = best.X; current.Y = best.Y;
                        individual.CalculateFitness();
                        improved = true;
                    }
                    else
                    {
                        current.X = oldX; current.Y = oldY;
                    }
                }

                if (!improved) break;
            }

            CompactDownAndLeft(individual);
            individual.CalculateFitness();
        }

        private Individual CloneIndividual(Individual source)
        {
            var rects = source.Rectangles.Select(r => r.Clone()).ToList();
            return new Individual(rects);
        }

        private void Shuffle<T>(List<T> list, Random random)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                T temp = list[i];
                list[i] = list[j];
                list[j] = temp;
            }
        }

        private struct Position
        {
            public int X; public int Y; public int Area;
            public Position(int x, int y, int area) { X = x; Y = y; Area = area; }
        }
    }
}
