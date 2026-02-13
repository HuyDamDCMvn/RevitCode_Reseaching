using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartTag.Services
{
    /// <summary>
    /// Loads and manages tagging/dimension rules from JSON files.
    /// Rules are loaded from Data/Rules folder at startup.
    /// </summary>
    public class RuleEngine
    {
        private static RuleEngine _instance;
        private static readonly object _lock = new();

        private List<TaggingRule> _taggingRules = new();
        private List<DimensionPattern> _dimensionPatterns = new();
        private string _dataPath;
        private bool _isLoaded;

        public static RuleEngine Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new RuleEngine();
                    }
                }
                return _instance;
            }
        }

        private RuleEngine() { }

        /// <summary>
        /// Initialize the rule engine with the data folder path.
        /// </summary>
        public void Initialize(string dataPath = null)
        {
            if (_isLoaded && dataPath == _dataPath) return;

            _dataPath = dataPath ?? FindDataPath();
            if (string.IsNullOrEmpty(_dataPath))
            {
                System.Diagnostics.Debug.WriteLine("RuleEngine: Data path not found");
                return;
            }

            LoadAllRules();
            _isLoaded = true;
        }

        /// <summary>
        /// Find the Data folder relative to the executing assembly.
        /// </summary>
        private string FindDataPath()
        {
            // Try multiple possible locations
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var assemblyDir = Path.GetDirectoryName(assembly.Location);

            var candidates = new[]
            {
                Path.Combine(assemblyDir, "Data"),
                Path.Combine(assemblyDir, "..", "Data"),
                Path.Combine(assemblyDir, "..", "..", "src", "SmartTag", "Data"),
                // Development path
                @"D:\03_DCMvn\RevitCode\src\SmartTag\Data"
            };

            foreach (var candidate in candidates)
            {
                if (Directory.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            return null;
        }

        /// <summary>
        /// Load all rules from the Data folder.
        /// </summary>
        private void LoadAllRules()
        {
            _taggingRules.Clear();
            _dimensionPatterns.Clear();

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            // Load tagging rules
            var taggingPath = Path.Combine(_dataPath, "Rules", "Tagging");
            if (Directory.Exists(taggingPath))
            {
                foreach (var file in Directory.GetFiles(taggingPath, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var rule = JsonSerializer.Deserialize<TaggingRule>(json, options);
                        if (rule != null && rule.Enabled)
                        {
                            rule.SourceFile = file;
                            _taggingRules.Add(rule);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to load rule {file}: {ex.Message}");
                    }
                }
            }

            // Load dimension patterns
            var dimensionPath = Path.Combine(_dataPath, "Rules", "Dimension");
            if (Directory.Exists(dimensionPath))
            {
                foreach (var file in Directory.GetFiles(dimensionPath, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var pattern = JsonSerializer.Deserialize<DimensionPattern>(json, options);
                        if (pattern != null && pattern.Enabled)
                        {
                            pattern.SourceFile = file;
                            _dimensionPatterns.Add(pattern);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to load pattern {file}: {ex.Message}");
                    }
                }
            }

            // Sort by priority
            _taggingRules = _taggingRules.OrderByDescending(r => r.Priority).ToList();

            System.Diagnostics.Debug.WriteLine($"RuleEngine: Loaded {_taggingRules.Count} tagging rules, {_dimensionPatterns.Count} dimension patterns");
        }

        /// <summary>
        /// Reload all rules (useful after editing rule files).
        /// </summary>
        public void Reload()
        {
            _isLoaded = false;
            Initialize(_dataPath);
        }

        /// <summary>
        /// Get tagging rules that match the given category.
        /// </summary>
        public IEnumerable<TaggingRule> GetTaggingRulesForCategory(string category)
        {
            return _taggingRules.Where(r => 
                r.Conditions?.Categories == null || 
                r.Conditions.Categories.Count == 0 ||
                r.Conditions.Categories.Any(c => 
                    c.Equals(category, StringComparison.OrdinalIgnoreCase) ||
                    category.Contains(c, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Get the best matching tagging rule for an element.
        /// </summary>
        public TaggingRule GetBestTaggingRule(string category, string familyName, string viewType)
        {
            return GetBestTaggingRule(category, familyName, viewType, null, null);
        }

        /// <summary>
        /// Get the best matching tagging rule for an element with system info.
        /// </summary>
        /// <param name="category">Revit category (e.g., "OST_PipeCurves")</param>
        /// <param name="familyName">Family name</param>
        /// <param name="viewType">View type (e.g., "FloorPlan")</param>
        /// <param name="systemClassification">Revit system classification (e.g., "SanitaryWaste")</param>
        /// <param name="systemName">Revit System Name parameter value (e.g., "HZG-HK-VL")</param>
        public TaggingRule GetBestTaggingRule(string category, string familyName, string viewType, 
            string systemClassification, string systemName)
        {
            foreach (var rule in _taggingRules)
            {
                if (MatchesRule(rule, category, familyName, viewType, systemClassification, systemName))
                {
                    return rule;
                }
            }
            return null;
        }

        /// <summary>
        /// Get all matching tagging rules for an element (for debugging/UI).
        /// </summary>
        public IEnumerable<TaggingRule> GetAllMatchingRules(string category, string familyName, string viewType,
            string systemClassification = null, string systemName = null)
        {
            return _taggingRules.Where(rule => 
                MatchesRule(rule, category, familyName, viewType, systemClassification, systemName));
        }

        private bool MatchesRule(TaggingRule rule, string category, string familyName, string viewType,
            string systemClassification = null, string systemName = null)
        {
            var conditions = rule.Conditions;
            if (conditions == null) return true;

            // Check categories
            if (conditions.Categories?.Count > 0)
            {
                if (!conditions.Categories.Any(c => 
                    category.Contains(c, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            // Check family name patterns
            if (conditions.FamilyNamePatterns?.Count > 0)
            {
                if (!conditions.FamilyNamePatterns.Any(pattern =>
                    System.Text.RegularExpressions.Regex.IsMatch(
                        familyName ?? "", 
                        pattern, 
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                {
                    return false;
                }
            }

            // Check view types
            if (conditions.ViewTypes?.Count > 0)
            {
                if (!conditions.ViewTypes.Any(vt => 
                    vt.Equals(viewType, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            // Check system classification (e.g., SanitaryWaste, DomesticColdWater)
            if (conditions.SystemClassification?.Count > 0 && !string.IsNullOrEmpty(systemClassification))
            {
                if (!conditions.SystemClassification.Any(sc => 
                    sc.Equals(systemClassification, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            // Check system name patterns (e.g., .*HZG.*, .*KLT.*)
            if (conditions.SystemNamePatterns?.Count > 0 && !string.IsNullOrEmpty(systemName))
            {
                if (!conditions.SystemNamePatterns.Any(pattern =>
                    System.Text.RegularExpressions.Regex.IsMatch(
                        systemName,
                        pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Get all dimension patterns for a discipline.
        /// </summary>
        public IEnumerable<DimensionPattern> GetDimensionPatterns(string discipline = "MEP")
        {
            return _dimensionPatterns.Where(p => 
                p.Discipline == "All" || 
                p.Discipline.Equals(discipline, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Get all loaded tagging rules.
        /// </summary>
        public IReadOnlyList<TaggingRule> TaggingRules => _taggingRules.AsReadOnly();

        /// <summary>
        /// Get all loaded dimension patterns.
        /// </summary>
        public IReadOnlyList<DimensionPattern> DimensionPatterns => _dimensionPatterns.AsReadOnly();

        /// <summary>
        /// Get the data folder path.
        /// </summary>
        public string DataPath => _dataPath;

        /// <summary>
        /// Get rules summary for diagnostics.
        /// </summary>
        public string GetRulesSummary()
        {
            var summary = new System.Text.StringBuilder();
            summary.AppendLine($"RuleEngine: {_taggingRules.Count} tagging rules loaded");
            summary.AppendLine("Priority | ID | Name | System");
            summary.AppendLine("---------|-----|------|--------");
            foreach (var rule in _taggingRules.Take(20))
            {
                summary.AppendLine($"{rule.Priority,8} | {rule.Id,-30} | {rule.Name,-40} | {rule.SystemConnection ?? "-"}");
            }
            if (_taggingRules.Count > 20)
            {
                summary.AppendLine($"... and {_taggingRules.Count - 20} more rules");
            }
            return summary.ToString();
        }

        /// <summary>
        /// Get tag format pattern from a rule's tagFormat JSON.
        /// </summary>
        public string GetTagPattern(TaggingRule rule, string formatKey = "pipeTag")
        {
            if (rule?.TagFormat == null || !rule.TagFormat.HasValue) return null;

            try
            {
                var tagFormat = rule.TagFormat.Value;
                if (tagFormat.TryGetProperty(formatKey, out var formatObj))
                {
                    if (formatObj.TryGetProperty("pattern", out var pattern))
                    {
                        return pattern.GetString();
                    }
                }
                // Try direct pattern property
                if (tagFormat.TryGetProperty("pattern", out var directPattern))
                {
                    return directPattern.GetString();
                }
            }
            catch
            {
                // Ignore parsing errors
            }
            return null;
        }

        /// <summary>
        /// Get elevation reference info from a rule.
        /// </summary>
        public (string Type, string Datum, string Format) GetElevationReference(TaggingRule rule, string systemType = null)
        {
            if (rule?.ElevationReference == null || !rule.ElevationReference.HasValue)
                return (null, null, null);

            try
            {
                var elevRef = rule.ElevationReference.Value;
                
                // Check for nested system-specific elevation (e.g., waterSupply, condensate)
                if (!string.IsNullOrEmpty(systemType) && elevRef.TryGetProperty(systemType, out var systemElev))
                {
                    return ExtractElevationInfo(systemElev);
                }
                
                // Try direct properties
                return ExtractElevationInfo(elevRef);
            }
            catch
            {
                // Ignore parsing errors
            }
            return (null, null, null);
        }

        private (string Type, string Datum, string Format) ExtractElevationInfo(JsonElement element)
        {
            string type = null, datum = null, format = null;
            
            if (element.TryGetProperty("type", out var typeEl))
                type = typeEl.GetString();
            if (element.TryGetProperty("datum", out var datumEl))
                datum = datumEl.GetString();
            if (element.TryGetProperty("pattern", out var patternEl))
                format = patternEl.GetString();
                
            return (type, datum, format);
        }
    }

    #region Rule Data Classes

    public class TaggingRule
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("priority")]
        public int Priority { get; set; } = 50;

        /// <summary>
        /// System connection type for dual-mode equipment discrimination (e.g., "HZG", "KLT")
        /// </summary>
        [JsonPropertyName("systemConnection")]
        public string SystemConnection { get; set; }

        [JsonPropertyName("conditions")]
        public TaggingConditions Conditions { get; set; }

        [JsonPropertyName("actions")]
        public TaggingActions Actions { get; set; }

        [JsonPropertyName("scoring")]
        public ScoringWeights Scoring { get; set; }

        /// <summary>
        /// Tag format configuration - patterns and examples
        /// </summary>
        [JsonPropertyName("tagFormat")]
        public JsonElement? TagFormat { get; set; }

        /// <summary>
        /// Elevation reference configuration (datum type, format)
        /// </summary>
        [JsonPropertyName("elevationReference")]
        public JsonElement? ElevationReference { get; set; }

        /// <summary>
        /// Metadata about the rule source and confidence
        /// </summary>
        [JsonPropertyName("metadata")]
        public RuleMetadata Metadata { get; set; }

        [JsonIgnore]
        public string SourceFile { get; set; }
    }

    public class RuleMetadata
    {
        [JsonPropertyName("author")]
        public string Author { get; set; }

        [JsonPropertyName("sources")]
        public List<string> Sources { get; set; }

        [JsonPropertyName("sampleCount")]
        public int SampleCount { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("updatedAt")]
        public string UpdatedAt { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }
    }

    public class TaggingConditions
    {
        [JsonPropertyName("categories")]
        public List<string> Categories { get; set; }

        [JsonPropertyName("familyNamePatterns")]
        public List<string> FamilyNamePatterns { get; set; }

        [JsonPropertyName("viewTypes")]
        public List<string> ViewTypes { get; set; }

        /// <summary>
        /// Revit pipe/duct system classification filter (e.g., "SanitaryWaste", "DomesticColdWater")
        /// </summary>
        [JsonPropertyName("systemClassification")]
        public List<string> SystemClassification { get; set; }

        /// <summary>
        /// Regex patterns to match Revit System Name parameter (e.g., ".*HZG.*", ".*KLT.*")
        /// </summary>
        [JsonPropertyName("systemNamePatterns")]
        public List<string> SystemNamePatterns { get; set; }
    }

    public class TaggingActions
    {
        [JsonPropertyName("preferredPositions")]
        public List<string> PreferredPositions { get; set; }

        [JsonPropertyName("offsetDistance")]
        public double OffsetDistance { get; set; } = 0.5;

        [JsonPropertyName("addLeader")]
        public bool AddLeader { get; set; } = true;

        [JsonPropertyName("leaderStyle")]
        public string LeaderStyle { get; set; } = "Straight";

        [JsonPropertyName("avoidCollisionWith")]
        public List<string> AvoidCollisionWith { get; set; }

        [JsonPropertyName("groupAlignment")]
        public string GroupAlignment { get; set; } = "None";
    }

    public class ScoringWeights
    {
        [JsonPropertyName("collisionPenalty")]
        public double CollisionPenalty { get; set; } = -100;

        [JsonPropertyName("preferenceBonus")]
        public double PreferenceBonus { get; set; } = 50;

        [JsonPropertyName("alignmentBonus")]
        public double AlignmentBonus { get; set; } = 30;

        [JsonPropertyName("leaderLengthPenalty")]
        public double LeaderLengthPenalty { get; set; } = -5;

        [JsonPropertyName("nearEdgeBonus")]
        public double NearEdgeBonus { get; set; } = 10;
    }

    public class DimensionPattern
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("discipline")]
        public string Discipline { get; set; } = "MEP";

        [JsonPropertyName("pattern")]
        public DimensionPatternConfig Pattern { get; set; }

        [JsonIgnore]
        public string SourceFile { get; set; }
    }

    public class DimensionPatternConfig
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("direction")]
        public string Direction { get; set; }

        [JsonPropertyName("dimensionTo")]
        public string DimensionTo { get; set; }

        [JsonPropertyName("grouping")]
        public GroupingConfig Grouping { get; set; }

        [JsonPropertyName("placement")]
        public PlacementConfig Placement { get; set; }
    }

    public class GroupingConfig
    {
        [JsonPropertyName("method")]
        public string Method { get; set; }

        [JsonPropertyName("tolerance")]
        public double Tolerance { get; set; }

        [JsonPropertyName("minElementsPerGroup")]
        public int MinElementsPerGroup { get; set; } = 1;
    }

    public class PlacementConfig
    {
        [JsonPropertyName("offset")]
        public double Offset { get; set; }

        [JsonPropertyName("side")]
        public string Side { get; set; }

        [JsonPropertyName("chainGap")]
        public double ChainGap { get; set; }
    }

    #endregion
}
