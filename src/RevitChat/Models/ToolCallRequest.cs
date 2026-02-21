using System.Collections.Generic;

namespace RevitChat.Models
{
    public class ToolCallRequest
    {
        public string ToolCallId { get; set; }
        public string FunctionName { get; set; }
        public Dictionary<string, object> Arguments { get; set; } = new();
    }
}
