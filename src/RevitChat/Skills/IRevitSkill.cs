using System.Collections.Generic;
using Autodesk.Revit.UI;
using OpenAI.Chat;

namespace RevitChat.Skills
{
    /// <summary>
    /// A modular capability module for the Revit AI chatbot.
    /// Each skill provides a set of OpenAI function tool definitions
    /// and knows how to execute them against the Revit API.
    /// 
    /// To add new capabilities, implement this interface and register
    /// the skill in SkillRegistry.RegisterDefaults().
    /// </summary>
    public interface IRevitSkill
    {
        string Name { get; }
        string Description { get; }
        IReadOnlyList<ChatTool> GetToolDefinitions();
        bool CanHandle(string functionName);
        string Execute(string functionName, UIApplication app, Dictionary<string, object> args);
    }
}
