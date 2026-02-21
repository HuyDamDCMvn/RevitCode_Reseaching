using System.Collections.Concurrent;
using System.Collections.Generic;
using RevitChat.Models;

namespace RevitChat.Handler
{
    /// <summary>
    /// Thread-safe queue for tool call requests.
    /// ViewModel enqueues, ExternalEvent handler dequeues.
    /// </summary>
    public class ChatRequestQueue
    {
        private readonly ConcurrentQueue<ToolCallRequest> _queue = new();

        public void Enqueue(ToolCallRequest request) => _queue.Enqueue(request);

        public void EnqueueAll(IEnumerable<ToolCallRequest> requests)
        {
            foreach (var r in requests) _queue.Enqueue(r);
        }

        public bool TryDequeue(out ToolCallRequest request) => _queue.TryDequeue(out request);

        public bool IsEmpty => _queue.IsEmpty;

        public void Clear()
        {
            while (_queue.TryDequeue(out _)) { }
        }
    }
}
