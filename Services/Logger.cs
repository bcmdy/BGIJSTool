using System.IO;
using System.Text;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace BGIJSTool.Services
{
    public class Logger : ILogger
    {
        // 预先创建并冻结画刷，避免每条日志都新建 BrushConverter 并解析颜色字符串。
        // 冻结后的画刷线程安全，可在所有日志行间共享。
        private static readonly Brush InfoBrush    = CreateFrozenBrush("#000000");
        private static readonly Brush SuccessBrush = CreateFrozenBrush("#008000");
        private static readonly Brush WarningBrush = CreateFrozenBrush("#FFA500");
        private static readonly Brush ErrorBrush   = CreateFrozenBrush("#FF0000");

        private readonly RichTextBox _logBox;
        private readonly string _logFilePath;

        public Logger(RichTextBox logBox, string programPath)
        {
            _logBox = logBox;
            LogDirectory = Path.Combine(programPath, "logs");
            Directory.CreateDirectory(LogDirectory);
            _logFilePath = Path.Combine(LogDirectory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
        }

        public string LogDirectory { get; }

        public void Log(string message, string type)
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var brush = type switch
            {
                "信息" => InfoBrush,
                "成功" => SuccessBrush,
                "警告" => WarningBrush,
                "错误" => ErrorBrush,
                _ => InfoBrush
            };

            var logEntry = $"[{time}] [{type}] {message}\n";
            AppendToLogBox(logEntry, brush);
            AppendToFile(logEntry);
        }

        public void LogInfo(string message) => Log(message, "信息");

        public void LogSuccess(string message) => Log(message, "成功");

        public void LogWarning(string message) => Log(message, "警告");

        public void LogError(string message) => Log(message, "错误");

        public void ClearCurrentLog()
        {
            _logBox.Dispatcher.Invoke(() => _logBox.Document.Blocks.Clear());
            File.WriteAllText(_logFilePath, string.Empty, Encoding.UTF8);
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
                var para = new Paragraph(run);
                _logBox.Document.Blocks.Add(para);
                _logBox.ScrollToEnd();
            });
        }

        private void AppendToFile(string text)
        {
            File.AppendAllText(_logFilePath, text, Encoding.UTF8);
        }
    }
}
