using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartTag.Models;

namespace SmartTag.Services
{
    /// <summary>
    /// Loads tag position patterns from Data/Patterns/TagPositions/*.json.
    /// Used to suggest positions based on professional drawing patterns (MunichRE, TUV).
    /// </summary>
    public class TagPositionPatternLoader
    {
        private static TagPositionPatternLoader _instance;
        private static readonly object _lock = new();

        private List<TagPositionPatternFile> _patternFiles = new();
        private string _dataPath;
        private bool _isLoaded;

        public static TagPositionPatternLoader Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new TagPositionPatternLoader();
                    }
                }
                return _instance;
            }
        }

        private TagPositionPatternLoader() { }

        /// <summary>
        /// Initialize and load all pattern files from Data/Patterns/TagPositions.
        /// </summary>
        public void Initialize(string dataPath = null)
        {
            if (_isLoaded && dataPath == _dataPath) return;

            _dataPath = dataPath ?? FindDataPath();
            if (string.IsNullOrEmpty(_dataPath))
            {
                System.Diagnostics.Debug.WriteLine("TagPositionPatternLoader: Data path not found");
                return;
            }

            LoadAllPatterns();
            _isLoaded = true;
        }

        private string FindDataPath()
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var assemblyDir = Path.GetDirectoryName(assembly.Location);
            var candidates = new[]
            {
                Path.Combine(assemblyDir, "Data"),
                Path.Combine(assemblyDir, "..", "Data"),
                Path.Combine(assemblyDir, "..", "..", "src", "SmartTag", "Data"),
                @"D:\03_DCMvn\RevitCode\src\SmartTag\Data"
            };
            foreach (var candidate in candidates)
            {
                if (Directory.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            return null;
        }

        private void LoadAllPatterns()
        {
            _patternFiles.Clear();
            var patternPath = Path.Combine(_dataPath, "Patterns", "TagPositions");
            if (!Directory.Exists(patternPath))
            {
                System.Diagnostics.Debug.WriteLine($"TagPositionPatternLoader: Path not found {patternPath}");
                return;
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            foreach (var file in Directory.GetFiles(patternPath, "*.json"))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith("_", StringComparison.Ordinal))
                    continue;

                try
                {
                    var json = File.ReadAllText(file);
                    var doc = JsonSerializer.Deserialize<TagPositionPatternFile>(json, options);
                    if (doc?.Observations != null && doc.Observations.Count > 0)
                    {
                        doc.SourceFile = file;
                        doc.DisciplineHint = GetDisciplineHintFromFileName(fileName);
                        doc.ScaleHint = ParseScaleFromSource(doc.Source);
                        _patternFiles.Add(doc);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"TagPositionPatternLoader: Failed to load {file}: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"TagPositionPatternLoader: Loaded {_patternFiles.Count} pattern files");
        }

        /// <summary>
        /// Get preferred positions and leader hint from patterns for an element.
        /// Matches by category and optional view scale.
        /// </summary>
        public PatternPlacementHint GetHint(string category, string systemName, int viewScale)
        {
            if (!_isLoaded)
                Initialize(_dataPath);

            var hint = new PatternPlacementHint();
            var categoryNorm = category ?? "";

            foreach (var file in _patternFiles)
            {
                if (!FileMatchesCategory(file, categoryNorm, systemName))
                    continue;
                if (!ScaleMatches(file.ScaleHint, viewScale))
                    continue;

                foreach (var obs in file.Observations)
                {
                    if (!ObservationMatchesCategory(obs, categoryNorm))
                        continue;

                    var position = ResolveTagPosition(obs);
                    var hasLeader = ResolveHasLeader(obs);
                    if (position != TagPosition.Auto)
                    {
                        if (!hint.Positions.Contains(position))
                            hint.Positions.Add(position);
                        hint.HasLeader = hasLeader ?? hint.HasLeader;
                    }
                }

                if (hint.Positions.Count > 0)
                    break;
            }

            if (hint.Positions.Count == 0)
                hint.Positions.Add(TagPosition.TopRight); // fallback

            return hint;
        }

        private bool FileMatchesCategory(TagPositionPatternFile file, string category, string systemName)
        {
            var hint = file.DisciplineHint ?? "";
            if (string.IsNullOrEmpty(hint))
                return true;

            // Map category to discipline keywords
            if (category.Contains("Pipe"))
            {
                if (hint.IndexOf("sanitary", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (hint.IndexOf("hvac", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (hint.IndexOf("heating", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (hint.IndexOf("refrigeration", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (hint.IndexOf("piping", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            if (category.Contains("Duct"))
            {
                if (hint.IndexOf("hvac", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (hint.IndexOf("rlt", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (hint.IndexOf("duct", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            if (category.Contains("CableTray") || category.Contains("Conduit"))
            {
                if (hint.IndexOf("cable", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (hint.IndexOf("electrical", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            if (category.Contains("Electrical") || category.Contains("Lighting"))
            {
                if (hint.IndexOf("electrical", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (hint.IndexOf("lightning", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            if (category.Contains("Opening") || category.Contains("GenericModel"))
            {
                if (hint.IndexOf("mep", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (hint.IndexOf("opening", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            return false;
        }

        private bool ObservationMatchesCategory(PatternObservation obs, string category)
        {
            if (string.IsNullOrEmpty(obs.Category))
                return true;
            return obs.Category.IndexOf(category, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool ScaleMatches(int? patternScale, int viewScale)
        {
            if (!patternScale.HasValue) return true;
            // Allow 1:50 vs 50, 1:100 vs 100, 1:200 vs 200
            if (patternScale.Value == viewScale) return true;
            if (viewScale >= 40 && viewScale <= 60 && patternScale.Value == 50) return true;
            if (viewScale >= 80 && viewScale <= 120 && patternScale.Value == 100) return true;
            if (viewScale >= 150 && viewScale <= 250 && patternScale.Value == 200) return true;
            return false;
        }

        private TagPosition ResolveTagPosition(PatternObservation obs)
        {
            // 1. Direct tagPosition field (e.g. "Left", "Center")
            if (!string.IsNullOrEmpty(obs.TagPosition))
            {
                if (Enum.TryParse<TagPosition>(obs.TagPosition, true, out var pos))
                    return pos;
            }

            // 2. Map position text to enum
            var positionText = (obs.Position ?? "").ToLowerInvariant();
            if (string.IsNullOrEmpty(positionText))
                return TagPosition.Auto;

            if (positionText.Contains("centerline") || positionText.Contains("along") && positionText.Contains("center"))
                return TagPosition.Center;
            if (positionText.Contains("center of room") || positionText.Contains("at panel center") ||
                positionText.Contains("at fixture center") || positionText.Contains("at each") ||
                positionText.Contains("at drain") || positionText.Contains("at grid"))
                return TagPosition.Center;
            if (positionText.Contains("left"))
                return TagPosition.Left;
            if (positionText.Contains("right"))
                return TagPosition.Right;
            if (positionText.Contains("above") || positionText.Contains("top"))
                return TagPosition.TopCenter;
            if (positionText.Contains("below") || positionText.Contains("bottom"))
                return TagPosition.BottomCenter;
            if (positionText.Contains("near") && positionText.Contains("pipe"))
                return TagPosition.TopCenter;
            if (positionText.Contains("near") && positionText.Contains("unit"))
                return TagPosition.TopRight;
            if (positionText.Contains("near") && positionText.Contains("equipment") || positionText.Contains("near") && positionText.Contains("valve"))
                return TagPosition.Right;
            if (positionText.Contains("near riser") || positionText.Contains("near busway") || positionText.Contains("at panel location"))
                return TagPosition.Right;
            if (positionText.Contains("near manifold"))
                return TagPosition.TopRight;
            if (positionText.Contains("aligned with run"))
                return TagPosition.TopCenter;
            if (positionText.Contains("sloped"))
                return TagPosition.Center;

            return TagPosition.Center;
        }

        private bool? ResolveHasLeader(PatternObservation obs)
        {
            if (obs.HasLeader.HasValue)
                return obs.HasLeader.Value;
            var p = (obs.Position ?? "").ToLowerInvariant();
            if (p.Contains("with leader") || p.Contains("leader to"))
                return true;
            if (p.Contains("centerline") || p.Contains("at fixture center") || p.Contains("at panel"))
                return false;
            return null;
        }

        private string GetDisciplineHintFromFileName(string fileName)
        {
            var name = Path.GetFileNameWithoutExtension(fileName) ?? "";
            return name.Replace("_tuv", "").Replace("_munichre", "").Replace("_", " ");
        }

        private int? ParseScaleFromSource(PatternSource source)
        {
            if (source == null)
                return null;

            var scaleStr = source.Scale ?? (source.Drawings != null && source.Drawings.Count > 0 ? source.Drawings[0].Scale : null) ?? "";
            if (string.IsNullOrEmpty(scaleStr))
                return null;

            // "1:50" -> 50, "1:100" -> 100
            var parts = scaleStr.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out var scale))
                return scale;
            if (int.TryParse(scaleStr.Trim(), out var s))
                return s;
            return null;
        }

        public void Reload()
        {
            _isLoaded = false;
            Initialize(_dataPath);
        }

        public int LoadedFileCount => _patternFiles?.Count ?? 0;

        #region Data classes (JSON)

        public class TagPositionPatternFile
        {
            [JsonPropertyName("source")]
            public PatternSource Source { get; set; }

            [JsonPropertyName("observations")]
            public List<PatternObservation> Observations { get; set; }

            [JsonIgnore]
            public string SourceFile { get; set; }

            [JsonIgnore]
            public string DisciplineHint { get; set; }

            [JsonIgnore]
            public int? ScaleHint { get; set; }
        }

        public class PatternSource
        {
            [JsonPropertyName("projectName")]
            public string ProjectName { get; set; }

            [JsonPropertyName("scale")]
            public string Scale { get; set; }

            [JsonPropertyName("drawings")]
            public List<PatternDrawing> Drawings { get; set; }
        }

        public class PatternDrawing
        {
            [JsonPropertyName("scale")]
            public string Scale { get; set; }

            [JsonPropertyName("discipline")]
            public string Discipline { get; set; }
        }

        public class PatternObservation
        {
            [JsonPropertyName("elementType")]
            public string ElementType { get; set; }

            [JsonPropertyName("category")]
            public string Category { get; set; }

            [JsonPropertyName("position")]
            public string Position { get; set; }

            [JsonPropertyName("tagPosition")]
            public string TagPosition { get; set; }

            [JsonPropertyName("hasLeader")]
            public bool? HasLeader { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// Result of pattern lookup: preferred positions and leader hint.
    /// </summary>
    public class PatternPlacementHint
    {
        public List<TagPosition> Positions { get; set; } = new();
        public bool HasLeader { get; set; } = true;
    }
}
