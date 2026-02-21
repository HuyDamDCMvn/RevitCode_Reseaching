using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RevitChat.Models
{
    public interface IChatService
    {
        bool IsInitialized { get; }
        void ClearHistory();

        event System.Action<string> DebugMessage;

        Task<(string assistantMessage, List<ToolCallRequest> toolCalls)> SendMessageAsync(
            string userMessage, CancellationToken ct = default);

        Task<(string assistantMessage, List<ToolCallRequest> toolCalls)> ContinueWithToolResultsAsync(
            Dictionary<string, string> toolResults, CancellationToken ct = default);
    }
}
