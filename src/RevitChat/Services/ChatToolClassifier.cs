using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace RevitChat.Services
{
    /// <summary>
    /// ONNX-based MLP classifier that predicts tool probabilities from prompt embeddings.
    /// Trained offline via tools/ChatTraining/train_tool_classifier.py.
    /// Falls back gracefully when model or index is unavailable.
    /// </summary>
    public sealed class ChatToolClassifier : IDisposable
    {
        private static ChatToolClassifier _instance;
        private static readonly object _lock = new();

        private InferenceSession _session;
        private string[] _toolNames;
        private int _embedDim;
        private bool _disposed;

        public bool IsAvailable => _session != null && _toolNames != null;

        public static ChatToolClassifier Instance
        {
            get
            {
                if (_instance == null)
                    lock (_lock) { _instance ??= new ChatToolClassifier(); }
                return _instance;
            }
        }

        private ChatToolClassifier() { }

        public void Initialize(string dllDirectory)
        {
            var modelsDir = Path.Combine(dllDirectory, "Data", "Models");
            var onnxPath = Path.Combine(modelsDir, "tool_classifier.onnx");
            var indexPath = Path.Combine(modelsDir, "tool_classifier_index.json");

            LoadIndex(indexPath);
            LoadModel(onnxPath);
        }

        private void LoadIndex(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                var json = File.ReadAllText(path);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("tool_names", out var namesEl))
                {
                    _toolNames = namesEl.EnumerateArray()
                        .Select(e => e.GetString())
                        .ToArray();
                }

                if (root.TryGetProperty("embed_dim", out var dimEl))
                    _embedDim = dimEl.GetInt32();

                System.Diagnostics.Debug.WriteLine(
                    $"ChatToolClassifier: Loaded index ({_toolNames?.Length ?? 0} tools, dim={_embedDim})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ChatToolClassifier: Index load failed: {ex.Message}");
            }
        }

        private void LoadModel(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                var options = new SessionOptions
                {
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = 1,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC
                };
                _session = new InferenceSession(path, options);
                System.Diagnostics.Debug.WriteLine($"ChatToolClassifier: ONNX model loaded from {path}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ChatToolClassifier: Model load failed: {ex.Message}");
                _session = null;
            }
        }

        /// <summary>
        /// Get tool probability scores from an embedding vector.
        /// Returns an empty dictionary if the classifier is unavailable.
        /// </summary>
        public Dictionary<string, double> GetToolScores(float[] embedding)
        {
            var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (!IsAvailable || embedding == null) return scores;

            var dim = _embedDim > 0 ? _embedDim : 768;
            if (embedding.Length != dim)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"ChatToolClassifier: Embedding dim mismatch ({embedding.Length} vs {dim})");
                return scores;
            }

            try
            {
                var inputTensor = new DenseTensor<float>(embedding, new[] { 1, dim });
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("embedding", inputTensor)
                };

                using var results = _session.Run(inputs);
                var output = results.First();
                var logits = output.AsTensor<float>();

                // Apply softmax to get probabilities
                var probs = Softmax(logits, _toolNames.Length);

                for (int i = 0; i < _toolNames.Length && i < probs.Length; i++)
                {
                    if (probs[i] > 0.01)
                        scores[_toolNames[i]] = probs[i];
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ChatToolClassifier: Inference failed: {ex.Message}");
            }

            return scores;
        }

        /// <summary>
        /// Get the top-K most likely tools for the given embedding.
        /// </summary>
        public List<(string tool, double probability)> GetTopTools(float[] embedding, int topK = 5)
        {
            var scores = GetToolScores(embedding);
            return scores
                .OrderByDescending(kv => kv.Value)
                .Take(topK)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
        }

        private static double[] Softmax(Tensor<float> logits, int count)
        {
            var values = new double[count];
            double maxVal = double.MinValue;

            for (int i = 0; i < count; i++)
            {
                values[i] = logits[0, i];
                if (values[i] > maxVal) maxVal = values[i];
            }

            double sumExp = 0;
            for (int i = 0; i < count; i++)
            {
                values[i] = Math.Exp(values[i] - maxVal);
                sumExp += values[i];
            }

            if (sumExp > 0)
            {
                for (int i = 0; i < count; i++)
                    values[i] /= sumExp;
            }

            return values;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _session?.Dispose();
            _session = null;
        }
    }
}
