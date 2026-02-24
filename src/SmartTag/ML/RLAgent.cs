using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SmartTag.Models;
using SmartTag.Services;

namespace SmartTag.ML
{
    /// <summary>
    /// ONNX-based RL agent for tag placement refinement.
    /// Falls back to RLPolicyLibrary JSON lookup when no ONNX model is available.
    /// </summary>
    public class RLAgent : IDisposable
    {
        private InferenceSession _session;
        private readonly string _modelPath;
        private readonly Random _rng = new();
        private bool _disposed;

        public const int StateDim = 50;
        public const int ActionDim = 12;

        public static readonly string[] ActionNames =
        {
            "TopRight", "TopLeft", "TopCenter",
            "BottomRight", "BottomLeft", "BottomCenter",
            "Right", "Left", "Center",
            "AlignRow", "AlignColumn", "ToggleLeader"
        };

        public bool IsOnnxAvailable => _session != null;

        public RLAgent(string modelPath = null)
        {
            _modelPath = modelPath
                ?? DataPathResolver.Resolve("Models/placement_policy.onnx");

            TryLoadModel();
        }

        private void TryLoadModel()
        {
            if (string.IsNullOrEmpty(_modelPath) || !File.Exists(_modelPath))
                return;

            try
            {
                var options = new SessionOptions
                {
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = 1,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC
                };
                _session = new InferenceSession(_modelPath, options);
                System.Diagnostics.Debug.WriteLine($"RLAgent: ONNX model loaded from {_modelPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RLAgent: Failed to load ONNX model: {ex.Message}");
                _session = null;
            }
        }

        /// <summary>
        /// Get the best action index from the policy network.
        /// </summary>
        public int GetAction(float[] state)
        {
            var qValues = GetQValues(state);
            if (qValues == null)
                return -1;

            int best = 0;
            for (int i = 1; i < qValues.Length; i++)
            {
                if (qValues[i] > qValues[best])
                    best = i;
            }
            return best;
        }

        /// <summary>
        /// Epsilon-greedy action selection for exploration.
        /// </summary>
        public int GetActionWithExploration(float[] state, double epsilon = 0.1)
        {
            if (_rng.NextDouble() < epsilon)
                return _rng.Next(ActionDim);

            return GetAction(state);
        }

        /// <summary>
        /// Get Q-values for all actions given a state vector.
        /// Returns null if ONNX model is not available.
        /// </summary>
        public float[] GetQValues(float[] state)
        {
            if (_session == null || state == null)
                return null;

            try
            {
                var inputTensor = new DenseTensor<float>(state, new[] { 1, StateDim });
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("state", inputTensor)
                };

                using var results = _session.Run(inputs);
                var output = results.First();
                var tensor = output.AsTensor<float>();
                var qValues = new float[ActionDim];
                for (int i = 0; i < ActionDim; i++)
                    qValues[i] = tensor[0, i];
                return qValues;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RLAgent: Inference failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get position scores from either ONNX model or JSON policy fallback.
        /// Returns a dictionary of TagPosition -> normalized score (0..1).
        /// </summary>
        public Dictionary<TagPosition, double> GetPositionScores(
            TaggableElement element,
            ElementContext context,
            float[] features)
        {
            var scores = new Dictionary<TagPosition, double>();

            if (IsOnnxAvailable && features != null)
            {
                var state = BuildState(features);
                var qValues = GetQValues(state);
                if (qValues != null)
                {
                    var positionQValues = qValues.Take(9).ToArray();
                    var min = positionQValues.Min();
                    var max = positionQValues.Max();
                    var range = max - min;

                    for (int i = 0; i < 9; i++)
                    {
                        if (Enum.TryParse<TagPosition>(ActionNames[i], out var pos))
                        {
                            scores[pos] = range > 1e-6
                                ? (positionQValues[i] - min) / range
                                : 0.5;
                        }
                    }
                    return scores;
                }
            }

            // Fallback: use JSON policy library
            var policy = RLPolicyLibrary.Instance.GetBestPolicy(element, context);
            if (policy != null)
            {
                foreach (TagPosition pos in Enum.GetValues(typeof(TagPosition)))
                {
                    if (pos == TagPosition.Auto) continue;
                    var normalized = policy.GetNormalizedPositionScore(pos);
                    if (normalized.HasValue)
                        scores[pos] = normalized.Value;
                }
            }

            return scores;
        }

        /// <summary>
        /// Build the full 50-dim state vector from 20-dim element features.
        /// Remaining dimensions are set to 0 (KNN scores, existing tags, collision map, alignment).
        /// </summary>
        public static float[] BuildState(float[] elementFeatures, float[] knnScores = null)
        {
            var state = new float[StateDim];

            if (elementFeatures != null)
            {
                var len = Math.Min(elementFeatures.Length, 20);
                Array.Copy(elementFeatures, 0, state, 0, len);
            }

            if (knnScores != null)
            {
                var len = Math.Min(knnScores.Length, 5);
                Array.Copy(knnScores, 0, state, 20, len);
            }

            return state;
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
