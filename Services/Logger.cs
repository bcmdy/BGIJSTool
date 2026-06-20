using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace BGIJSTool.Services
{
    public class Logger : ILogger
    {
        // 日志级别标签：集中定义，供级别方法与颜色映射共用，避免散落的字符串字面量。
        public const string LevelInfo    = "信息";
        public const string LevelSuccess = "成功";
        public const string LevelWarning = "警告";
        public const string LevelError   = "错误";

        // 级别 -> 颜色画刷映射。画刷预创建并冻结，线程安全且避免逐行重复解析颜色字符串。
        private static readonly IReadOnlyDictionary<string, Brush> LevelBrushes =
            new Dictionary<string, Brush>
            {
                [LevelInfo]    = CreateFrozenBrush("#000000"),
                [LevelSuccess] = CreateFrozenBrush("#008000"),
                [LevelWarning] = CreateFrozenBrush("#FFA500"),
                [LevelError]   = CreateFrozenBrush("#FF0000"),
            };

        private readonly RichTextBox _logBox;

        public Logger(RichTextBox logBox, string programPath)
        {
            _logBox = logBox;
            LogDirectory = Path.Combine(programPath, "logs");
            Directory.CreateDirectory(LogDirectory);
        }

        public string LogDirectory { get; }

        // 按写入时刻的日期决定文件名，避免程序跨午夜运行后仍写入前一天的日志文件。
        private string CurrentLogFilePath
            => Path.Combine(LogDirectory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");

        public void Log(string message, string type)
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var brush = LevelBrushes.TryGetValue(type, out var b) ? b : LevelBrushes[LevelInfo];

            var line = $"[{time}] [{type}] {message}";
            AppendToLogBox(line, brush);   // 每条日志单独成段，无需再加换行符
            AppendToFile(line + "\n");      // 写入文件时补换行，保证每条独立成行
        }

        public void LogInfo(string message) => Log(message, LevelInfo);

        public void LogSuccess(string message) => Log(message, LevelSuccess);

        public void LogWarning(string message) => Log(message, LevelWarning);

        public void LogError(string message) => Log(message, LevelError);

        public void ClearCurrentLog()
        {
            _logBox.Dispatcher.Invoke(() => _logBox.Document.Blocks.Clear());
            File.WriteAllText(CurrentLogFilePath, string.Empty, Encoding.UTF8);
        }

        private static Brush CreateFrozenBrush(string color)
        {
            var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
            brush.Freeze();
            return brush;
        }

        private void AppendToLogBox(string text, Brush brush)
        {
            _logBox.Dispatcher.Invoke(() =>
            {
                var run = new Run(text)
                {
                    Foreground = brush
                };
                var para = new Paragraph(run) { Margin = new Thickness(0) };
                _logBox.Document.Blocks.Add(para);
                _logBox.ScrollToEnd();
            });
        }

        private void AppendToFile(string text)
        {
            File.AppendAllText(CurrentLogFilePath, text, Encoding.UTF8);
        }
    }
}
