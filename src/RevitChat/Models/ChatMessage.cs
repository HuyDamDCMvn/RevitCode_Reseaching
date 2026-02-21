using System;

namespace RevitChat.Models
{
    public enum ChatRole
    {
        User,
        Assistant,
        System,
        Tool
    }

    public class ChatMessage
    {
        public ChatRole Role { get; set; }
        public string Content { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public bool IsToolCall { get; set; }
        public string ToolName { get; set; }

        public static ChatMessage FromUser(string content) =>
            new() { Role = ChatRole.User, Content = content };

        public static ChatMessage FromAssistant(string content) =>
            new() { Role = ChatRole.Assistant, Content = content };

        public static ChatMessage Thinking() =>
            new() { Role = ChatRole.Assistant, Content = "...", IsToolCall = true };

        public static ChatMessage ToolProgress(string toolName) =>
            new() { Role = ChatRole.Tool, Content = $"Calling {toolName}...", IsToolCall = true, ToolName = toolName };
    }
}
