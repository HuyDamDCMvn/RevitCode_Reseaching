using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RevitChat.Services
{
    public class CompanyStandardsService
    {
        private readonly object _loadLock = new();
        private Dictionary<string, string> _namingPatterns = new();
        private Dictionary<string, List<string>> _requiredParameters = new();
        private List<string> _forbiddenFamilies = new();
        private int _maxWarnings = 500;
        private int _maxUnusedFamiliesPercent = 30;

        public IReadOnlyDictionary<string, string> NamingPatterns => _namingPatterns;
        public IReadOnlyDictionary<string, List<string>> RequiredParameters => _requiredParameters;
        public IReadOnlyList<string> ForbiddenFamilies => _forbiddenFamilies;
        public int MaxWarnings => _maxWarnings;
        public int MaxUnusedFamiliesPercent => _maxUnusedFamiliesPercent;

        public void Load(string configDir)
        {
            lock (_loadLock)
            {
            var path = Path.Combine(configDir, "company_standards.json");
            if (!File.Exists(path)) return;

            try
            {
                var json = File.ReadAllText(path);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("naming_patterns", out var np))
                {
                    _namingPatterns = np.EnumerateObject()
                        .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
                }

                if (root.TryGetProperty("required_parameters", out var rp))
                {
                    _requiredParameters = rp.EnumerateObject()
                        .ToDictionary(p => p.Name, p => p.Value.EnumerateArray()
                            .Select(v => v.GetString()).Where(s => s != null).ToList());
                }

                if (root.TryGetProperty("forbidden_families", out var ff))
                    _forbiddenFamilies = ff.EnumerateArray().Select(v => v.GetString()).Where(s => s != null).ToList();

                if (root.TryGetProperty("max_warnings", out var mw)) _maxWarnings = mw.GetInt32();
                if (root.TryGetProperty("max_unused_families_percent", out var mu)) _maxUnusedFamiliesPercent = mu.GetInt32();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CompanyStandardsService] Load: {ex.Message}");
            }
            }
        }

        public bool IsNamingValid(string category, string name)
        {
            lock (_loadLock)
            {
            if (!_namingPatterns.TryGetValue(category, out var pattern)) return true;
            try { return System.Text.RegularExpressions.Regex.IsMatch(name, pattern); }
            catch { return true; }
            }
        }

        public bool IsFamilyForbidden(string familyName)
        {
            lock (_loadLock)
            {
                return _forbiddenFamilies.Any(f => f.Equals(familyName, StringComparison.OrdinalIgnoreCase));
            }
        }

        public List<string> GetMissingParameters(string category, IEnumerable<string> existingParams)
        {
            lock (_loadLock)
            {
                if (!_requiredParameters.TryGetValue(category, out var required)) return new List<string>();
                var existing = new HashSet<string>(existingParams, StringComparer.OrdinalIgnoreCase);
                return required.Where(r => !existing.Contains(r)).ToList();
            }
        }
    }
}
