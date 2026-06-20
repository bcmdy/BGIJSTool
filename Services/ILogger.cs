namespace BGIJSTool.Services
{
    public interface ILogger
    {
        void Log(string message, string type);
        void LogInfo(string message);
        void LogSuccess(string message);
        void LogWarning(string message);
        void LogError(string message);
    }
}
