using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitChat.Services
{
    /// <summary>
    /// Carries over context from previous conversation turns so users don't
    /// have to repeat category/system/level in follow-up messages.
    /// </summary>
    public sealed class ConversationContextTracker
    {
        private string _lastCategory;
        private List<string> _lastCategories = new();
        private string _lastSystem;
        private string _lastLevel;
        private List<long> _lastElementIds = new();
        private PromptIntent _lastIntent = PromptIntent.Unknown;
        private DateTime _lastUpdate = DateTime.MinValue;

        private static readonly TimeSpan CarryoverTtl = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Fill null/empty fields in <paramref name="current"/> from previous turn,
        /// but only when the prompt contains a reference keyword ("same", "tương tự", etc.)
        /// or when the current turn has the same intent as the previous one.
        /// Call with the original user message for reference keyword detection.
        /// </summary>
        public void ApplyCarryover(PromptContext current, string userMessage = null)
        {
            if (current == null || DateTime.UtcNow - _lastUpdate > CarryoverTtl) return;

            bool hasRef = HasReferenceKeyword(userMessage) ||
                          (_lastIntent != PromptIntent.Unknown && current.PrimaryIntent == _lastIntent);

            if (string.IsNullOrEmpty(current.DetectedCategory) && !string.IsNullOrEmpty(_lastCategory) && hasRef)
            {
                current.DetectedCategory = _lastCategory;
                if (current.DetectedCategories.Count == 0 && _lastCategories.Count > 0)
                    current.DetectedCategories = new List<string>(_lastCategories);
            }

            if (string.IsNullOrEmpty(current.DetectedSystem) && !string.IsNullOrEmpty(_lastSystem) && hasRef)
                current.DetectedSystem = _lastSystem;

            if (string.IsNullOrEmpty(current.DetectedLevel) && !string.IsNullOrEmpty(_lastLevel) && hasRef)
                current.DetectedLevel = _lastLevel;

            if (current.DetectedElementIds.Count == 0 && _lastElementIds.Count > 0 && hasRef)
                current.DetectedElementIds = new List<long>(_lastElementIds);

            // Regenerate hint after carryover
            if (current.DetectedCategory != null || current.DetectedSystem != null)
                current.ContextHint = FormatHintAfterCarryover(current);
        }

        /// <summary>
        /// Update tracker state from the current turn's analysis results.
        /// </summary>
        public void Update(PromptContext ctx)
        {
            if (ctx == null) return;
            _lastUpdate = DateTime.UtcNow;

            if (ctx.PrimaryIntent != PromptIntent.Unknown)
                _lastIntent = ctx.PrimaryIntent;

            if (!string.IsNullOrEmpty(ctx.DetectedCategory))
            {
                _lastCategory = ctx.DetectedCategory;
                if (ctx.DetectedCategories.Count > 0)
                    _lastCategories = new List<string>(ctx.DetectedCategories);
            }

            if (!string.IsNullOrEmpty(ctx.DetectedSystem))
                _lastSystem = ctx.DetectedSystem;

            if (!string.IsNullOrEmpty(ctx.DetectedLevel))
                _lastLevel = ctx.DetectedLevel;

            if (ctx.DetectedElementIds.Count > 0)
                _lastElementIds = new List<long>(ctx.DetectedElementIds);
        }

        public void Reset()
        {
            _lastCategory = null;
            _lastCategories = new();
            _lastSystem = null;
            _lastLevel = null;
            _lastElementIds = new();
            _lastIntent = PromptIntent.Unknown;
            _lastUpdate = DateTime.MinValue;
        }

        private static bool HasReferenceKeyword(string hint)
        {
            if (string.IsNullOrEmpty(hint)) return false;
            var lower = hint.ToLowerInvariant();
            var stripped = PromptAnalyzer.StripDiacriticsPublic(lower);
            return PromptAnalyzer.CarryoverKeywords.Any(kw =>
                lower.Contains(kw) || stripped.Contains(kw));
        }

        private static string FormatHintAfterCarryover(PromptContext ctx)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[Context Analysis — with carryover]");
            if (ctx.PrimaryIntent != PromptIntent.Unknown)
                sb.AppendLine($"- Intent: {ctx.PrimaryIntent}");
            if (ctx.DetectedCategory != null)
                sb.AppendLine($"- Category: {ctx.DetectedCategory}");
            if (ctx.DetectedSystem != null)
                sb.AppendLine($"- System: {ctx.DetectedSystem}");
            if (ctx.DetectedLevel != null)
                sb.AppendLine($"- Level: {ctx.DetectedLevel}");
            if (ctx.DetectedNumbers.Count > 0)
                sb.AppendLine($"- Numbers: {string.Join(", ", ctx.DetectedNumbers.Select(n => $"{n.Value}{n.Unit}"))}");
            if (ctx.DetectedDryRun)
                sb.AppendLine("- Mode: DRY RUN");
            if (ctx.IsAmbiguous)
                sb.AppendLine("- WARNING: Ambiguous category, ask user to clarify.");
            if (ctx.SuggestedTools.Count > 0)
                sb.AppendLine($"- Suggested tools: {string.Join(", ", ctx.SuggestedTools)}");
            return sb.ToString().TrimEnd();
        }
    }
}
