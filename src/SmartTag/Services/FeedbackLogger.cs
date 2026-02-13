using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartTag.Services
{
    /// <summary>
    /// Logs user feedback (approve/reject/correct) for training data collection.
    /// </summary>
    public class FeedbackLogger
    {
        private static FeedbackLogger _instance;
        private static readonly object _lock = new();

        private string _feedbackPath;
        private string _sessionId;
        private readonly JsonSerializerOptions _jsonOptions;

        public static FeedbackLogger Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new FeedbackLogger();
                    }
                }
                return _instance;
            }
        }

        private FeedbackLogger()
        {
            _sessionId = Guid.NewGuid().ToString("N")[..8];
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
        }

        /// <summary>
        /// Initialize with the feedback folder path.
        /// </summary>
        public void Initialize(string feedbackPath = null)
        {
            _feedbackPath = feedbackPath ?? FindFeedbackPath();
            
            if (!string.IsNullOrEmpty(_feedbackPath))
            {
                Directory.CreateDirectory(Path.Combine(_feedbackPath, "approved"));
                Directory.CreateDirectory(Path.Combine(_feedbackPath, "rejected"));
                Directory.CreateDirectory(Path.Combine(_feedbackPath, "corrections"));
            }
        }

        private string FindFeedbackPath()
        {
            var ruleEngine = RuleEngine.Instance;
            if (!string.IsNullOrEmpty(ruleEngine.DataPath))
            {
                return Path.Combine(ruleEngine.DataPath, "Feedback");
            }
            return null;
        }

        /// <summary>
        /// Log an approved placement.
        /// </summary>
        public void LogApproved(FeedbackEntry entry)
        {
            entry.Status = "Approved";
            entry.Timestamp = DateTime.UtcNow;
            entry.SessionId = _sessionId;
            SaveFeedback(entry, "approved");
        }

        /// <summary>
        /// Log a rejected placement.
        /// </summary>
        public void LogRejected(FeedbackEntry entry, string reason = null)
        {
            entry.Status = "Rejected";
            entry.Timestamp = DateTime.UtcNow;
            entry.SessionId = _sessionId;
            if (!string.IsNullOrEmpty(reason))
            {
                entry.Correction = new CorrectionInfo { Reason = reason };
            }
            SaveFeedback(entry, "rejected");
        }

        /// <summary>
        /// Log a user correction (most valuable for training).
        /// </summary>
        public void LogCorrection(FeedbackEntry entry)
        {
            entry.Status = "Corrected";
            entry.Timestamp = DateTime.UtcNow;
            entry.SessionId = _sessionId;
            SaveFeedback(entry, "corrections");
        }

        private void SaveFeedback(FeedbackEntry entry, string subfolder)
        {
            if (string.IsNullOrEmpty(_feedbackPath)) return;

            try
            {
                entry.Id = Guid.NewGuid().ToString();
                var json = JsonSerializer.Serialize(entry, _jsonOptions);
                
                var folder = Path.Combine(_feedbackPath, subfolder);
                var filename = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{entry.Id[..8]}.json";
                var filepath = Path.Combine(folder, filename);
                
                File.WriteAllText(filepath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save feedback: {ex.Message}");
            }
        }

        /// <summary>
        /// Get count of feedback entries by status.
        /// </summary>
        public (int Approved, int Rejected, int Corrections) GetFeedbackCounts()
        {
            if (string.IsNullOrEmpty(_feedbackPath)) return (0, 0, 0);

            int approved = CountFiles(Path.Combine(_feedbackPath, "approved"));
            int rejected = CountFiles(Path.Combine(_feedbackPath, "rejected"));
            int corrections = CountFiles(Path.Combine(_feedbackPath, "corrections"));

            return (approved, rejected, corrections);
        }

        private int CountFiles(string folder)
        {
            if (!Directory.Exists(folder)) return 0;
            return Directory.GetFiles(folder, "*.json").Length;
        }

        /// <summary>
        /// Start a new session (e.g., when window opens).
        /// </summary>
        public void StartNewSession()
        {
            _sessionId = Guid.NewGuid().ToString("N")[..8];
        }

        public string SessionId => _sessionId;
    }

    #region Feedback Data Classes

    public class FeedbackEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } // "Tag" or "Dimension"

        [JsonPropertyName("status")]
        public string Status { get; set; } // "Approved", "Rejected", "Corrected"

        [JsonPropertyName("context")]
        public ContextInfo Context { get; set; }

        [JsonPropertyName("element")]
        public ElementInfo Element { get; set; }

        [JsonPropertyName("originalPlacement")]
        public PlacementInfo OriginalPlacement { get; set; }

        [JsonPropertyName("correction")]
        public CorrectionInfo Correction { get; set; }

        [JsonPropertyName("nearbyElements")]
        public List<NearbyElementInfo> NearbyElements { get; set; }

        [JsonPropertyName("metadata")]
        public MetadataInfo Metadata { get; set; }
    }

    public class ContextInfo
    {
        [JsonPropertyName("projectName")]
        public string ProjectName { get; set; }

        [JsonPropertyName("viewName")]
        public string ViewName { get; set; }

        [JsonPropertyName("viewType")]
        public string ViewType { get; set; }

        [JsonPropertyName("viewScale")]
        public double ViewScale { get; set; }

        [JsonPropertyName("discipline")]
        public string Discipline { get; set; }
    }

    public class ElementInfo
    {
        [JsonPropertyName("elementId")]
        public long ElementId { get; set; }

        [JsonPropertyName("category")]
        public string Category { get; set; }

        [JsonPropertyName("familyName")]
        public string FamilyName { get; set; }

        [JsonPropertyName("typeName")]
        public string TypeName { get; set; }

        [JsonPropertyName("boundingBox")]
        public BoundingBoxInfo BoundingBox { get; set; }

        [JsonPropertyName("parameters")]
        public Dictionary<string, object> Parameters { get; set; }
    }

    public class BoundingBoxInfo
    {
        [JsonPropertyName("minX")]
        public double MinX { get; set; }

        [JsonPropertyName("minY")]
        public double MinY { get; set; }

        [JsonPropertyName("maxX")]
        public double MaxX { get; set; }

        [JsonPropertyName("maxY")]
        public double MaxY { get; set; }
    }

    public class PlacementInfo
    {
        [JsonPropertyName("tagId")]
        public long? TagId { get; set; }

        [JsonPropertyName("position")]
        public PositionInfo Position { get; set; }

        [JsonPropertyName("positionType")]
        public string PositionType { get; set; }

        [JsonPropertyName("hasLeader")]
        public bool HasLeader { get; set; }

        [JsonPropertyName("score")]
        public double Score { get; set; }

        [JsonPropertyName("ruleUsed")]
        public string RuleUsed { get; set; }
    }

    public class PositionInfo
    {
        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }
    }

    public class CorrectionInfo
    {
        [JsonPropertyName("newPosition")]
        public PositionInfo NewPosition { get; set; }

        [JsonPropertyName("newPositionType")]
        public string NewPositionType { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; }

        [JsonPropertyName("notes")]
        public string Notes { get; set; }
    }

    public class NearbyElementInfo
    {
        [JsonPropertyName("elementId")]
        public long ElementId { get; set; }

        [JsonPropertyName("category")]
        public string Category { get; set; }

        [JsonPropertyName("distance")]
        public double Distance { get; set; }

        [JsonPropertyName("direction")]
        public string Direction { get; set; }
    }

    public class MetadataInfo
    {
        [JsonPropertyName("userName")]
        public string UserName { get; set; }

        [JsonPropertyName("machineName")]
        public string MachineName { get; set; }

        [JsonPropertyName("revitVersion")]
        public string RevitVersion { get; set; }

        [JsonPropertyName("toolVersion")]
        public string ToolVersion { get; set; }
    }

    #endregion
}
