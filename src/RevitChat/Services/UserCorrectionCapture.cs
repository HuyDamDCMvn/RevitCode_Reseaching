using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitChat.Services
{
    /// <summary>
    /// Detects user corrections ("no, I meant pipes", "sai rồi, ý tôi là...") and
    /// captures them as negative/positive signals for self-learning.
    /// </summary>
    public static class UserCorrectionCapture
    {
        private static readonly Regex CorrectionPatternEn = new(
            @"(?:no|not|wrong|incorrect)\s*[,.]?\s*(?:i\s+meant?|i\s+want(?:ed)?|it\s+should\s+be|please\s+use)\s+(.+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CorrectionPatternVn = new(
            @"(?:không|khong|sai|sai rồi|sai roi|nhầm|nham)\s*[,.]?\s*(?:ý tôi là|y toi la|tôi muốn|toi muon|phải là|phai la|dùng|dung|nên là|nen la)\s+(.+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] NegativeKeywordsEn =
            { "no", "wrong", "not what i", "that's wrong", "incorrect", "not right" };
        private static readonly string[] NegativeKeywordsVn =
            { "không", "khong", "sai", "sai rồi", "sai roi", "nhầm", "nham",
              "không phải", "khong phai", "không đúng", "khong dung" };

        private static readonly string[] RetryKeywordsEn =
            { "try again", "redo", "do it again", "one more time" };
        private static readonly string[] RetryKeywordsVn =
            { "làm lại", "lam lai", "thử lại", "thu lai", "lần nữa", "lan nua" };

        /// <summary>
        /// Check if a user message is a correction of the previous response.
        /// Returns the corrected intent text if detected, null otherwise.
        /// </summary>
        public static CorrectionResult Detect(string userMessage, string previousPrompt,
            List<string> previousToolNames)
        {
            if (string.IsNullOrWhiteSpace(userMessage)) return null;

            var lower = userMessage.ToLowerInvariant();
            var stripped = PromptAnalyzer.StripDiacriticsPublic(lower);

            // Explicit correction with replacement
            var matchEn = CorrectionPatternEn.Match(lower);
            if (matchEn.Success)
            {
                return new CorrectionResult
                {
                    IsCorrection = true,
                    CorrectedText = matchEn.Groups[1].Value.Trim(),
                    PreviousPrompt = previousPrompt,
                    PreviousTools = previousToolNames,
                    ConfidenceScore = 0.9
                };
            }

            var matchVn = CorrectionPatternVn.Match(stripped);
            if (matchVn.Success)
            {
                return new CorrectionResult
                {
                    IsCorrection = true,
                    CorrectedText = matchVn.Groups[1].Value.Trim(),
                    PreviousPrompt = previousPrompt,
                    PreviousTools = previousToolNames,
                    ConfidenceScore = 0.9
                };
            }

            // Implicit negative (no replacement text)
            bool isNegative = NegativeKeywordsEn.Any(k => stripped.Contains(k)) ||
                              NegativeKeywordsVn.Any(k => stripped.Contains(k));
            if (isNegative)
            {
                return new CorrectionResult
                {
                    IsCorrection = true,
                    IsNegativeOnly = true,
                    PreviousPrompt = previousPrompt,
                    PreviousTools = previousToolNames,
                    ConfidenceScore = 0.7
                };
            }

            // Retry detection
            bool isRetry = RetryKeywordsEn.Any(k => stripped.Contains(k)) ||
                           RetryKeywordsVn.Any(k => stripped.Contains(k));
            if (isRetry)
            {
                return new CorrectionResult
                {
                    IsRetry = true,
                    PreviousPrompt = previousPrompt,
                    PreviousTools = previousToolNames,
                    ConfidenceScore = 0.5
                };
            }

            return null;
        }

        /// <summary>
        /// Save the correction signal to feedback service.
        /// </summary>
        public static void SaveCorrection(CorrectionResult result)
        {
            if (result == null || !result.IsCorrection) return;

            if (result.PreviousTools != null)
            {
                foreach (var tool in result.PreviousTools)
                {
                    ChatFeedbackService.SaveCorrection(
                        result.PreviousPrompt, tool, null);
                }
            }

            if (!string.IsNullOrWhiteSpace(result.CorrectedText))
            {
                var newCtx = PromptAnalyzer.Analyze(result.CorrectedText);
                if (newCtx.SuggestedTools.Count > 0)
                {
                    var tools = newCtx.SuggestedTools.Select(t => new ToolUsage { Name = t }).ToList();
                    ChatFeedbackService.SaveApproved(result.CorrectedText, tools);
                }
            }
        }
    }

    public class CorrectionResult
    {
        public bool IsCorrection { get; set; }
        public bool IsNegativeOnly { get; set; }
        public bool IsRetry { get; set; }
        public string CorrectedText { get; set; }
        public string PreviousPrompt { get; set; }
        public List<string> PreviousTools { get; set; }
        public double ConfidenceScore { get; set; }
    }
}
