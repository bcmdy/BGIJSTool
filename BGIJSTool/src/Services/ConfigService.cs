using System.IO;
using System.Text.Json;
using BGIJSTool.Models;

namespace BGIJSTool.Services
{
    public class ConfigService
    {
        private readonly string _configPath;
        private Config _config = new();

        public ConfigService(string configPath)
        {
            _configPath = configPath;
        }

        public Config LoadConfig()
        {
            if (!File.Exists(_configPath))
            {
                throw new FileNotFoundException($"配置文件不存在: {_configPath}");
            }

            var json = File.ReadAllText(_configPath);
            _config = JsonSerializer.Deserialize<Config>(json) ?? new Config();
            return _config;
        }

        public bool IsValidPath()
        {
            return !string.IsNullOrEmpty(_config?.BGIpath) && Directory.Exists(_config.BGIpath);
        }

        public string GetBGIPath()
        {
            return _config?.BGIpath ?? string.Empty;
        }

        public System.Collections.Generic.List<Models.Module> GetModules()
        {
            return _config?.modules ?? new();
        }

        /// <summary>将 BGIpath 写回 config.json</summary>
        public void SaveBGIPath(string path)
        {
            _config.BGIpath = path;
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }

        /// <summary>配置文件的完整路径（仅供外部读取，不写入字段）</summary>
        public string ConfigPath => _configPath;
    }
}
