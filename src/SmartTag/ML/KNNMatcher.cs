using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SmartTag.Models;

namespace SmartTag.ML
{
    /// <summary>
    /// K-Nearest Neighbors matcher for finding similar tag placement examples.
    /// Uses training data from annotated professional drawings.
    /// </summary>
    public class KNNMatcher
    {
        private List<TrainingSample> _samples;
        private Dictionary<string, List<TrainingSample>> _samplesByCategory;
        private readonly FeatureExtractor _featureExtractor;

        public KNNMatcher()
        {
            _samples = new List<TrainingSample>();
            _samplesByCategory = new Dictionary<string, List<TrainingSample>>(StringComparer.OrdinalIgnoreCase);
            _featureExtractor = new FeatureExtractor();
        }

        #region Data Loading

        /// <summary>
        /// Load training data from JSON files.
        /// </summary>
        public void LoadTrainingData(string folderPath)
        {
            _samples.Clear();

            if (!Directory.Exists(folderPath))
            {
                System.Diagnostics.Debug.WriteLine($"Training data folder not found: {folderPath}");
                return;
            }

            var jsonFiles = Directory.GetFiles(folderPath, "*.json", SearchOption.AllDirectories);

            foreach (var file in jsonFiles)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var data = JsonSerializer.Deserialize<TrainingDataFile>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (data?.Samples != null)
                    {
                        foreach (var sample in data.Samples)
                        {
                            sample.SourceFile = file;
                            sample.ViewScale = data.Source?.ViewScale ?? 100;
                            _samples.Add(sample);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error loading {file}: {ex.Message}");
                }
            }

            RebuildCategoryIndex();
            System.Diagnostics.Debug.WriteLine($"KNNMatcher: Loaded {_samples.Count} training samples");
        }

        /// <summary>
        /// Add a single sample (for incremental learning).
        /// </summary>
        public void AddSample(TrainingSample sample)
        {
            if (sample == null) return;
            _samples.Add(sample);
            var cat = sample.Element?.Category ?? "Other";
            if (!_samplesByCategory.TryGetValue(cat, out var list))
            {
                list = new List<TrainingSample>();
                _samplesByCategory[cat] = list;
            }
            list.Add(sample);
        }

        private void RebuildCategoryIndex()
        {
            _samplesByCategory.Clear();
            foreach (var sample in _samples)
            {
                var cat = sample.Element?.Category ?? "Other";
                if (!_samplesByCategory.TryGetValue(cat, out var list))
                {
                    list = new List<TrainingSample>();
                    _samplesByCategory[cat] = list;
                }
                list.Add(sample);
            }
        }

        #endregion

        #region KNN Search

        /// <summary>
        /// Find K nearest neighbors for given features.
        /// </summary>
        public List<(TrainingSample sample, double distance)> FindKNearest(float[] features, int k = 5)
        {
            if (_samples.Count == 0 || features == null)
                return new List<(TrainingSample, double)>();

            var distances = new List<(TrainingSample sample, double distance)>();

            foreach (var sample in _samples)
            {
                var sampleFeatures = ExtractFeaturesFromSample(sample);
                var distance = EuclideanDistance(features, sampleFeatures);
                distances.Add((sample, distance));
            }

            // Return K nearest
            return distances
                .OrderBy(d => d.distance)
                .Take(k)
                .ToList();
        }

        /// <summary>
        /// Find K nearest neighbors with category filter.
        /// </summary>
        public List<(TrainingSample sample, double distance)> FindKNearestByCategory(
            float[] features, 
            string category, 
            int k = 5)
        {
            if (_samples.Count == 0 || features == null)
                return new List<(TrainingSample, double)>();

            // O(m) where m = samples in category, instead of O(n) filtering all samples
            if (!_samplesByCategory.TryGetValue(category ?? "Other", out var filteredSamples)
                || filteredSamples.Count == 0)
            {
                return FindKNearest(features, k);
            }

            var distances = new List<(TrainingSample sample, double distance)>(filteredSamples.Count);

            foreach (var sample in filteredSamples)
            {
                var sampleFeatures = ExtractFeaturesFromSample(sample);
                var distance = EuclideanDistance(features, sampleFeatures);
                distances.Add((sample, distance));
            }

            return distances
                .OrderBy(d => d.distance)
                .Take(k)
                .ToList();
        }

        #endregion

        #region Voting

        /// <summary>
        /// Vote for best tag position based on K nearest neighbors.
        /// </summary>
        public TagPositionVote Vote(List<(TrainingSample sample, double distance)> neighbors)
        {
            if (neighbors == null || neighbors.Count == 0)
                return new TagPositionVote();

            var vote = new TagPositionVote();
            var positionScores = new Dictionary<TagPosition, double>();
            double totalWeight = 0;

            foreach (var (sample, distance) in neighbors)
            {
                // Weight inversely proportional to distance
                double weight = 1.0 / (distance + 0.001);
                totalWeight += weight;

                var position = ParsePosition(sample.Tag?.Position);
                
                if (!positionScores.ContainsKey(position))
                    positionScores[position] = 0;
                
                positionScores[position] += weight;

                // Accumulate offset (weighted average)
                vote.AverageOffsetX += (sample.Tag?.OffsetX ?? 0) * weight;
                vote.AverageOffsetY += (sample.Tag?.OffsetY ?? 0) * weight;
                
                if (sample.Tag?.HasLeader == true)
                    vote.LeaderVotes += weight;
                else
                    vote.NoLeaderVotes += weight;
            }

            // Normalize
            if (totalWeight > 0)
            {
                vote.AverageOffsetX /= totalWeight;
                vote.AverageOffsetY /= totalWeight;
            }

            // Get position ranking
            vote.PositionScores = positionScores
                .OrderByDescending(p => p.Value)
                .ToDictionary(p => p.Key, p => p.Value / totalWeight);

            vote.BestPosition = vote.PositionScores.FirstOrDefault().Key;
            vote.Confidence = vote.PositionScores.FirstOrDefault().Value;
            vote.HasLeader = vote.LeaderVotes > vote.NoLeaderVotes;

            return vote;
        }

        #endregion

        #region Helpers

        private float[] ExtractFeaturesFromSample(TrainingSample sample)
        {
            return _featureExtractor.ExtractFeaturesFromRaw(
                sample.Element?.Category ?? "Other",
                sample.Element?.Orientation ?? 0,
                sample.Element?.Length ?? 0,
                sample.Element?.Width ?? 0,
                sample.Element?.Height ?? 0,
                sample.Element?.IsLinear ?? false,
                sample.Context?.Density ?? "medium",
                sample.Context?.HasNeighborAbove ?? false,
                sample.Context?.HasNeighborBelow ?? false,
                sample.Context?.HasNeighborLeft ?? false,
                sample.Context?.HasNeighborRight ?? false,
                sample.Context?.DistanceToWall ?? 10.0,
                sample.Context?.ParallelElementsCount ?? 0
            );
        }

        private double EuclideanDistance(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return double.MaxValue;

            double sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                var diff = a[i] - b[i];
                sum += diff * diff;
            }
            return Math.Sqrt(sum);
        }

        private TagPosition ParsePosition(string position)
        {
            if (string.IsNullOrEmpty(position))
                return TagPosition.TopRight;

            return position switch
            {
                "TopLeft" => TagPosition.TopLeft,
                "TopCenter" => TagPosition.TopCenter,
                "TopRight" => TagPosition.TopRight,
                "Left" => TagPosition.Left,
                "Center" => TagPosition.Center,
                "Right" => TagPosition.Right,
                "BottomLeft" => TagPosition.BottomLeft,
                "BottomCenter" => TagPosition.BottomCenter,
                "BottomRight" => TagPosition.BottomRight,
                _ => TagPosition.TopRight
            };
        }

        #endregion

        #region Properties

        public int SampleCount => _samples.Count;

        public IReadOnlyList<TrainingSample> Samples => _samples.AsReadOnly();

        #endregion
    }

    /// <summary>
    /// Result of KNN voting for tag position.
    /// </summary>
    public class TagPositionVote
    {
        public TagPosition BestPosition { get; set; }
        public double Confidence { get; set; }
        public Dictionary<TagPosition, double> PositionScores { get; set; } = new();
        public double AverageOffsetX { get; set; }
        public double AverageOffsetY { get; set; }
        public bool HasLeader { get; set; }
        public double LeaderVotes { get; set; }
        public double NoLeaderVotes { get; set; }
    }

    #region Data Models

    /// <summary>
    /// Training data file format.
    /// </summary>
    public class TrainingDataFile
    {
        public string Version { get; set; }
        public TrainingSource Source { get; set; }
        public List<TrainingSample> Samples { get; set; }
    }

    public class TrainingSource
    {
        public string Project { get; set; }
        public string Discipline { get; set; }
        public List<string> Drawings { get; set; }
        public int ViewScale { get; set; }
        public string AnnotatedBy { get; set; }
        public string AnnotatedDate { get; set; }
    }

    public class TrainingSample
    {
        public string Id { get; set; }
        public TrainingElement Element { get; set; }
        public TrainingContext Context { get; set; }
        public TrainingTag Tag { get; set; }
        
        // Runtime properties
        public string SourceFile { get; set; }
        public int ViewScale { get; set; }
    }

    public class TrainingElement
    {
        public string Category { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public double Orientation { get; set; }
        public bool IsLinear { get; set; }
        public double Length { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Diameter { get; set; }
        public string SystemType { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
    }

    public class TrainingContext
    {
        public string Density { get; set; }
        public int NeighborCount { get; set; }
        public bool HasNeighborAbove { get; set; }
        public bool HasNeighborBelow { get; set; }
        public bool HasNeighborLeft { get; set; }
        public bool HasNeighborRight { get; set; }
        public double DistanceToNearestAbove { get; set; }
        public double DistanceToNearestBelow { get; set; }
        public double DistanceToNearestLeft { get; set; }
        public double DistanceToNearestRight { get; set; }
        public double DistanceToWall { get; set; }
        public int ParallelElementsCount { get; set; }
        public bool IsInGroup { get; set; }
    }

    public class TrainingTag
    {
        public string Position { get; set; }
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public bool HasLeader { get; set; }
        public double LeaderLength { get; set; }
        public string Rotation { get; set; }
        public bool AlignedWithRow { get; set; }
        public bool AlignedWithColumn { get; set; }
        public string RowId { get; set; }
        public string ColumnId { get; set; }
        public string TagText { get; set; }
        public double TagWidth { get; set; }
        public double TagHeight { get; set; }
    }

    #endregion
}
