using System;
using System.IO;
using System.Reflection;

namespace SmartTag.Services
{
    /// <summary>
    /// Centralized path resolution for data files (Training, Rules, Patterns, Models).
    /// Eliminates duplicated path resolution and machine-specific hardcoded paths.
    /// </summary>
    public static class DataPathResolver
    {
        private static string _assemblyDir;

        private static string AssemblyDir
        {
            get
            {
                if (string.IsNullOrEmpty(_assemblyDir))
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    _assemblyDir = Path.GetDirectoryName(assembly.Location) ?? "";
                }
                return _assemblyDir;
            }
        }

        /// <summary>
        /// Resolve a data file path. Searches assembly-relative locations only.
        /// Falls back to temp directory if nothing found.
        /// </summary>
        /// <param name="relativePath">Relative path from the Data root, e.g. "Training/learned_overrides.json"</param>
        /// <param name="tempFallbackName">Filename for temp fallback, e.g. "SmartTag_learned_overrides.json"</param>
        public static string Resolve(string relativePath, string tempFallbackName = null)
        {
            var candidates = new[]
            {
                Path.Combine(AssemblyDir, "Data", relativePath),
                Path.Combine(AssemblyDir, "..", "Data", relativePath),
                Path.Combine(AssemblyDir, "..", "..", "src", "SmartTag", "Data", relativePath),
                Path.Combine(Environment.CurrentDirectory, "Data", relativePath),
            };

            foreach (var path in candidates)
            {
                try
                {
                    var full = Path.GetFullPath(path);
                    var dir = Path.GetDirectoryName(full);
                    if (dir != null && Directory.Exists(dir))
                        return full;
                }
                catch { }
            }

            if (string.IsNullOrEmpty(tempFallbackName))
                tempFallbackName = "SmartTag_" + Path.GetFileName(relativePath);

            return Path.Combine(Path.GetTempPath(), tempFallbackName);
        }

        /// <summary>
        /// Resolve a folder path (e.g. "Training/annotated").
        /// Returns the first existing folder, or null if none found.
        /// </summary>
        public static string ResolveFolder(string relativeFolderPath)
        {
            var candidates = new[]
            {
                Path.Combine(AssemblyDir, "Data", relativeFolderPath),
                Path.Combine(AssemblyDir, "..", "Data", relativeFolderPath),
                Path.Combine(AssemblyDir, "..", "..", "src", "SmartTag", "Data", relativeFolderPath),
                Path.Combine(Environment.CurrentDirectory, "Data", relativeFolderPath),
            };

            foreach (var path in candidates)
            {
                try
                {
                    var full = Path.GetFullPath(path);
                    if (Directory.Exists(full))
                        return full;
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Get the annotated folder sibling to a given file path.
        /// </summary>
        public static string GetAnnotatedFolder(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            var dir = Path.GetDirectoryName(filePath);
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "annotated");
        }
    }
}
