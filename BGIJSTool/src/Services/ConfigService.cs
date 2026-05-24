using System.Text.Json;
using System.IO;
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
            var options = new JsonSerializerOptions();
            options.Converters.Add(new Models.ConfigConverter());
            _config = JsonSerializer.Deserialize<Config>(json, options) ?? new Config();
            return _config;
        }

        public bool IsValidPath()
        {
            return !string.IsNullOrEmpty(_config.BGIpath) && Directory.Exists(_config.BGIpath);
        }

        public string GetBGIPath() => _config.BGIpath;

        public System.Collections.Generic.List<Models.Module> GetModules() => _config.modules;

        /// <summary>将 BGIpath 写回 config.json</summary>
        public void SaveBGIPath(string path)
        {
            _config.BGIpath = path;
            var options = new JsonSerializerOptions { WriteIndented = true };
            options.Converters.Add(new Models.ConfigConverter());
            var json = JsonSerializer.Serialize(_config, options);
            File.WriteAllText(_configPath, json);
        }

        /// <summary>配置文件的完整路径（仅供外部读取，不写入字段）</summary>
        public string ConfigPath => _configPath;
    }
}
