using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RevitChat.Models
{
    public interface IChatService
    {
        bool IsInitialized { get; }
        void ClearHistory();
        void RepairHistoryAfterCancel();

        event System.Action<string> DebugMessage;
        event System.Action<string> TokenReceived;

        Task<(string assistantMessage, List<ToolCallRequest> toolCalls)> SendMessageAsync(
            string userMessage, CancellationToken ct = default);

        Task<(string assistantMessage, List<ToolCallRequest> toolCalls)> ContinueWithToolResultsAsync(
            Dictionary<string, string> toolResults, CancellationToken ct = default);

        List<string> ValidateToolCalls(List<ToolCallRequest> toolCalls);

        Task<(string assistantMessage, List<ToolCallRequest> toolCalls)> RetryWithValidationErrorAsync(
            List<string> errors, CancellationToken ct = default);
    }

    /// <summary>
    /// Optional interface for chat services that support embedding-based learning.
    /// </summary>
    public interface IEmbeddingCapable
    {
        Task StoreEmbeddingAsync(string prompt, string toolName,
            Dictionary<string, object> args, string intent, CancellationToken ct = default);
    }
}
