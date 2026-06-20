using System.Collections.Generic;
using BGIJSTool.Services;

namespace BGIJSTool.Tests;

/// <summary>测试用日志器：仅记录消息，不依赖任何 UI。</summary>
internal sealed class FakeLogger : ILogger
{
    public List<string> Messages { get; } = new();

    public void Log(string message, string type) => Messages.Add($"[{type}] {message}");
    public void LogInfo(string message) => Log(message, "信息");
    public void LogSuccess(string message) => Log(message, "成功");
    public void LogWarning(string message) => Log(message, "警告");
    public void LogError(string message) => Log(message, "错误");
}
