using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
                throw new FileNotFoundException($"Config file does not exist: {_configPath}");

            var json = File.ReadAllText(_configPath);
            var validation = ValidateJson(json);
            if (validation.HasErrors)
                throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors));

            var options = new JsonSerializerOptions();
            options.Converters.Add(new ConfigConverter());
            _config = JsonSerializer.Deserialize<Config>(json, options) ?? new Config();
            return _config;
        }

        public ConfigValidationResult ValidateConfig()
        {
            if (!File.Exists(_configPath))
            {
                return new ConfigValidationResult(
                    new[] { $"Config file does not exist: {_configPath}" },
                    Array.Empty<string>());
            }

            return ValidateJson(File.ReadAllText(_configPath));
        }

        public bool IsValidPath()
        {
            return !string.IsNullOrEmpty(_config.BGIpath) && Directory.Exists(_config.BGIpath);
        }

        public string GetBGIPath() => _config.BGIpath;

        public List<Module> GetModules() => _config.modules;

        public void SaveBGIPath(string path)
        {
            _config.BGIpath = path;
            var options = CreateWriteOptions();

            if (File.Exists(_configPath))
            {
                var root = JsonNode.Parse(File.ReadAllText(_configPath)) as JsonObject;
                if (root is not null)
                {
                    root["BGIpath"] = path;
                    File.WriteAllText(_configPath, root.ToJsonString(options), Encoding.UTF8);
                    return;
                }
            }

            options.Converters.Add(new ConfigConverter());
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, options), Encoding.UTF8);
        }

        public string ConfigPath => _configPath;

        private static JsonSerializerOptions CreateWriteOptions()
        {
            return new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
        }

        private static ConfigValidationResult ValidateJson(string json)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                return new ConfigValidationResult(
                    new[] { $"Invalid config JSON: {ex.Message}" },
                    Array.Empty<string>());
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    errors.Add("Config root must be an object.");

                if (!root.TryGetProperty("BGIpath", out var bgiPath) || bgiPath.ValueKind != JsonValueKind.String)
                {
                    errors.Add("Config field BGIpath is required and must be a string.");
                }
                else if (string.IsNullOrWhiteSpace(bgiPath.GetString()))
                {
                    warnings.Add("Config field BGIpath is empty.");
                }

                if (!root.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array)
                {
                    errors.Add("Config field modules is required and must be an array.");
                    return new ConfigValidationResult(errors, warnings);
                }

                if (modules.GetArrayLength() == 0)
                    warnings.Add("Config contains no modules.");

                var moduleIndex = 0;
                foreach (var module in modules.EnumerateArray())
                {
                    moduleIndex++;
                    if (module.ValueKind != JsonValueKind.Object)
                    {
                        errors.Add($"Module #{moduleIndex} must be an object.");
                        continue;
                    }

                    var moduleName = ReadString(module, "name");
                    var displayName = DisplayName(moduleName, moduleIndex);
                    if (string.IsNullOrWhiteSpace(moduleName))
                        warnings.Add($"Module #{moduleIndex} has an empty name.");

                    if (!module.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                    {
                        errors.Add($"Module '{displayName}' must contain a files array.");
                        continue;
                    }

                    if (files.GetArrayLength() == 0)
                        warnings.Add($"Module '{displayName}' has no steps.");

                    var seenPathsByOp = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                    var stepIndex = 0;
                    foreach (var step in files.EnumerateArray())
                    {
                        stepIndex++;
                        if (step.ValueKind != JsonValueKind.Object)
                        {
                            errors.Add($"Module '{displayName}' step #{stepIndex} must be an object.");
                            continue;
                        }

                        var op = ReadString(step, "op");
                        if (string.IsNullOrWhiteSpace(op))
                        {
                            errors.Add($"Module '{displayName}' step #{stepIndex} is missing op.");
                        }
                        else if (!Enum.TryParse<OpType>(op, ignoreCase: false, out _))
                        {
                            errors.Add($"Module '{displayName}' step #{stepIndex} has unknown op: {op}.");
                        }

                        if (!step.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array)
                        {
                            errors.Add($"Module '{displayName}' step #{stepIndex} must contain a paths array.");
                            continue;
                        }

                        if (paths.GetArrayLength() == 0)
                            warnings.Add($"Module '{displayName}' step #{stepIndex} has no paths.");

                        foreach (var path in paths.EnumerateArray())
                        {
                            if (path.ValueKind != JsonValueKind.String)
                            {
                                errors.Add($"Module '{displayName}' step #{stepIndex} has a non-string path.");
                                continue;
                            }

                            var value = path.GetString() ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(value))
                            {
                                warnings.Add($"Module '{displayName}' step #{stepIndex} has an empty path.");
                                continue;
                            }

                            if (string.IsNullOrWhiteSpace(op))
                                continue;

                            if (!seenPathsByOp.TryGetValue(op, out var seenPaths))
                            {
                                seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                seenPathsByOp[op] = seenPaths;
                            }

                            if (!seenPaths.Add(value.Replace('\\', '/')))
                                warnings.Add($"Module '{displayName}' contains duplicate path in '{op}': {value}");
                        }
                    }
                }
            }

            return new ConfigValidationResult(errors, warnings);
        }

        private static string? ReadString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static string DisplayName(string? moduleName, int moduleIndex)
        {
            return string.IsNullOrWhiteSpace(moduleName) ? $"#{moduleIndex}" : moduleName;
        }
    }

    public sealed class ConfigValidationResult
    {
        public ConfigValidationResult(IEnumerable<string> errors, IEnumerable<string> warnings)
        {
            Errors = errors.ToList();
            Warnings = warnings.ToList();
        }

        public List<string> Errors { get; }
        public List<string> Warnings { get; }
        public bool HasErrors => Errors.Count > 0;
        public bool HasWarnings => Warnings.Count > 0;
    }
}
