using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using RevitChat.Handler;
using RevitChat.Models;

namespace RevitChat.Services
{
    public class ToolExecutionService
    {
        private static readonly HashSet<string> ReadOnlyTools = new()
        {
            "get_elements", "count_elements", "get_element_parameters", "search_elements",
            "get_levels", "get_categories", "get_current_view", "get_rooms", "get_project_info",
            "get_mep_systems", "get_duct_summary", "get_pipe_summary", "get_model_warnings",
            "get_model_statistics", "get_linked_models", "get_sheets_summary", "get_schedule_data",
            "get_grids", "get_levels_detailed", "get_worksets", "get_phases", "get_materials",
            "get_revisions", "get_view_filters", "get_view_templates", "get_family_types",
            "get_shared_parameters", "get_project_parameters", "get_current_selection",
            "get_tag_rules", "get_available_tag_types",
            "get_element_host", "get_element_connections", "get_element_geometry",
            "find_elements_near", "get_wall_layers", "get_view_crop_region",
            "compare_views", "check_clashes", "check_clearance",
            "get_clash_summary", "find_overlapping", "get_mep_elevation_table",
            "check_velocity", "check_insulation_coverage", "check_noise_level",
            "check_access_panel", "get_critical_path", "analyze_pressure_loss",
            "get_flow_distribution", "traverse_mep_network", "get_ceiling_grid",
            "audit_model_standards", "find_duplicate_elements", "audit_room_enclosure",
            "get_panel_schedules", "get_circuit_loads", "check_panel_capacity",
            "get_voltage_drop", "get_phase_balance", "get_structural_model",
            "check_rebar_coverage", "get_rebar_schedule", "get_building_schedules",
            "get_space_energy_data", "get_loaded_addins", "get_addin_load_times",
            "get_empty_tags", "get_untagged_elements", "screenshot_view",
            "get_ifc_mappings"
        };

        private readonly ConcurrentDictionary<string, (DateTime Time, string Result)> _resultCache = new();
        private const int CacheTtlSeconds = 60;

        private readonly ExternalEvent _externalEvent;
        private readonly RevitChatHandler _handler;
        private readonly ChatRequestQueue _queue;
        private readonly WorkingMemory _workingMemory = new();

        private readonly Action<Dictionary<string, string>> _onCompleted;
        private readonly Action<string> _onError;

        public event Action<string> OnProgress;
        public event Action OnModelModified;

        public void ReportProgress(string message) => OnProgress?.Invoke(message);

        private TaskCompletionSource<Dictionary<string, string>> _toolResultsTcs;
        private readonly SemaphoreSlim _execLock = new(1, 1);

        public WorkingMemory WorkingMemory => _workingMemory;

        public void InvalidateCache() => _resultCache.Clear();

        public ToolExecutionService(ExternalEvent externalEvent, RevitChatHandler handler, ChatRequestQueue queue)
        {
            _externalEvent = externalEvent;
            _handler = handler;
            _queue = queue;

            _onCompleted = results => _toolResultsTcs?.TrySetResult(results);
            _onError = error => _toolResultsTcs?.TrySetException(
                new InvalidOperationException($"Revit handler error: {error}"));

            _handler.OnToolCallsCompleted += _onCompleted;
            _handler.OnError += _onError;
        }

        public async Task<Dictionary<string, string>> ExecuteAsync(
            List<ToolCallRequest> toolCalls, int timeoutMs, CancellationToken ct)
        {
            if (!await _execLock.WaitAsync(0, ct))
                throw new InvalidOperationException("Another tool execution is already in progress.");

            try
            {
                var results = new Dictionary<string, string>();
                var toExecute = new List<ToolCallRequest>();
                var cutoff = DateTime.UtcNow.AddSeconds(-CacheTtlSeconds);

                foreach (var req in toolCalls)
                {
                    if (ReadOnlyTools.Contains(req.FunctionName))
                    {
                        var key = GetCacheKey(req.FunctionName, req.Arguments);
                        if (_resultCache.TryGetValue(key, out var cached) && cached.Time > cutoff)
                        {
                            results[req.ToolCallId] = cached.Result;
                            continue;
                        }
                    }
                    toExecute.Add(req);
                }

                if (toExecute.Count > 0)
                {
                    _toolResultsTcs = new TaskCompletionSource<Dictionary<string, string>>();
                    using var ctReg = ct.Register(() => _toolResultsTcs.TrySetCanceled());

                    _queue.Clear();
                    _queue.EnqueueAll(toExecute);
                    _externalEvent.Raise();

                    using var timeoutCts = new CancellationTokenSource(timeoutMs);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                    try
                    {
                        var timeoutTask = Task.Delay(Timeout.Infinite, linkedCts.Token);
                        var completed = await Task.WhenAny(_toolResultsTcs.Task, timeoutTask);

                        if (completed != _toolResultsTcs.Task)
                        {
                            _toolResultsTcs.TrySetCanceled();
                            if (ct.IsCancellationRequested)
                                throw new OperationCanceledException("Tool execution was cancelled.", ct);
                            throw new TimeoutException("Tool execution timed out. Revit may be busy or a modal dialog is open.");
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _toolResultsTcs.TrySetCanceled();
                        throw new TimeoutException("Tool execution timed out. Revit may be busy or a modal dialog is open.");
                    }

                    var execResults = await _toolResultsTcs.Task;
                    foreach (var kvp in execResults)
                        results[kvp.Key] = kvp.Value;

                    var hasModify = toExecute.Any(t => !ReadOnlyTools.Contains(t.FunctionName));
                    if (hasModify)
                    {
                        _resultCache.Clear();
                        OnModelModified?.Invoke();
                    }
                    else
                    {
                        foreach (var req in toExecute)
                        {
                            if (ReadOnlyTools.Contains(req.FunctionName) && results.TryGetValue(req.ToolCallId, out var res)
                                && !string.IsNullOrEmpty(res) && !res.Contains("\"error\""))
                            {
                                var key = GetCacheKey(req.FunctionName, req.Arguments);
                                _resultCache[key] = (DateTime.UtcNow, res);
                            }
                        }
                    }
                }

                return results;
            }
            finally
            {
                _execLock.Release();
            }
        }

        public void CancelPending()
        {
            _toolResultsTcs?.TrySetCanceled();
        }

        public void UpdateMemory(List<ToolCallRequest> toolCalls, Dictionary<string, string> results)
        {
            foreach (var tc in toolCalls)
            {
                if (results.TryGetValue(tc.ToolCallId, out var result))
                    _workingMemory.UpdateFromToolResult(tc.FunctionName, result);
            }
        }

        private static string GetCacheKey(string functionName, Dictionary<string, object> args)
        {
            var sorted = args != null
                ? args.OrderBy(kvp => kvp.Key).ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                : new Dictionary<string, object>();
            return functionName + JsonSerializer.Serialize(sorted);
        }

        public static string EnrichErrorContext(string functionName, string errorJson)
        {
            if (string.IsNullOrWhiteSpace(errorJson)) return errorJson;
            var lower = errorJson.ToLowerInvariant();
            if (lower.Contains("element not found"))
                return errorJson + " Some element IDs may be invalid. Try get_elements or search_elements first.";
            if (lower.Contains("parameter is read-only"))
                return errorJson + " This parameter cannot be modified via API.";
            if (lower.Contains("transaction"))
                return errorJson + " Revit may be in edit mode or busy. Try again after finishing current operation.";
            if (lower.Contains("not found"))
                return errorJson + " The specified item was not found in the document.";
            return errorJson;
        }

        public static Dictionary<string, string> CompressAndTruncate(
            List<ToolCallRequest> toolCalls, Dictionary<string, string> results, int maxPerResult)
        {
            var compressed = new Dictionary<string, string>(results.Count);
            foreach (var kvp in results)
            {
                var toolName = toolCalls.FirstOrDefault(t => t.ToolCallId == kvp.Key)?.FunctionName ?? "unknown";
                var val = WorkingMemory.CompressToolResult(toolName, kvp.Value);

                if (val != null && val.Contains("\"error\""))
                    val = EnrichErrorContext(toolName, val);

                if (val != null && val.Length > maxPerResult)
                    val = val[..maxPerResult] + $"\n...[TRUNCATED — {kvp.Value?.Length ?? 0} chars total]";

                compressed[kvp.Key] = val;
            }
            return compressed;
        }

        public void Cleanup()
        {
            _handler.OnToolCallsCompleted -= _onCompleted;
            _handler.OnError -= _onError;
            _toolResultsTcs?.TrySetCanceled();
        }
    }
}
