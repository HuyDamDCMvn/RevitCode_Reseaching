using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using RevitChat.Handler;
using RevitChat.Models;

namespace RevitChat.Services
{
    public class ToolExecutionService
    {
        private readonly ExternalEvent _externalEvent;
        private readonly RevitChatHandler _handler;
        private readonly ChatRequestQueue _queue;
        private readonly WorkingMemory _workingMemory = new();

        private readonly Action<Dictionary<string, string>> _onCompleted;
        private readonly Action<string> _onError;

        private TaskCompletionSource<Dictionary<string, string>> _toolResultsTcs;
        private readonly SemaphoreSlim _execLock = new(1, 1);

        public WorkingMemory WorkingMemory => _workingMemory;

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
                _toolResultsTcs = new TaskCompletionSource<Dictionary<string, string>>();

                using var ctReg = ct.Register(() => _toolResultsTcs.TrySetCanceled());

                _queue.Clear();
                _queue.EnqueueAll(toolCalls);
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

                return await _toolResultsTcs.Task;
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

        public static Dictionary<string, string> CompressAndTruncate(
            List<ToolCallRequest> toolCalls, Dictionary<string, string> results, int maxPerResult)
        {
            var compressed = new Dictionary<string, string>(results.Count);
            foreach (var kvp in results)
            {
                var toolName = toolCalls.FirstOrDefault(t => t.ToolCallId == kvp.Key)?.FunctionName ?? "unknown";
                var val = WorkingMemory.CompressToolResult(toolName, kvp.Value);

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
