using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RevitChat.Models
{
    public enum ChatRole
    {
        User,
        Assistant,
        System,
        Tool
    }

    public enum FeedbackType
    {
        None = 0,
        ThumbsUp = 1,
        ThumbsDown = -1
    }

    public class ChatMessage : INotifyPropertyChanged
    {
        public ChatRole Role { get; set; }

        private string _content;
        public string Content
        {
            get => _content;
            set { _content = value; OnPropertyChanged(); }
        }

        public DateTime Timestamp { get; set; } = DateTime.Now;
        public bool IsToolCall { get; set; }
        public string ToolName { get; set; }

        public string AssociatedPrompt { get; set; }
        public List<string> AssociatedToolNames { get; set; }
        public List<ToolCallRequest> AssociatedToolCalls { get; set; }

        private FeedbackType _feedback = FeedbackType.None;
        public FeedbackType Feedback
        {
            get => _feedback;
            set { _feedback = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowFeedbackButtons)); }
        }

        public bool ShowFeedbackButtons =>
            Role == ChatRole.Assistant && !IsToolCall && Feedback == FeedbackType.None;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public static ChatMessage FromUser(string content) =>
            new() { Role = ChatRole.User, Content = content };

        public static ChatMessage FromAssistant(string content) =>
            new() { Role = ChatRole.Assistant, Content = content };

        public static ChatMessage ToolProgress(string toolName) =>
            new() { Role = ChatRole.Tool, Content = $"Calling {toolName}...", IsToolCall = true, ToolName = toolName };
    }
}
