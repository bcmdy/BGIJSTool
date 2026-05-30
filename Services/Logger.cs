using System.IO;
using System.Text;
using System.Windows.Controls;
using System.Windows.Documents;

namespace BGIJSTool.Services
{
    public class Logger : ILogger
    {
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
            var color = type switch
            {
                "信息" => "#000000",
                "成功" => "#008000",
                "警告" => "#FFA500",
                "错误" => "#FF0000",
                _ => "#000000"
            };

            var logEntry = $"[{time}] [{type}] {message}\n";
            AppendToLogBox(logEntry, color);
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

        private void AppendToLogBox(string text, string color)
        {
            _logBox.Dispatcher.Invoke(() =>
            {
                var brush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!;
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
