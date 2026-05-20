using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BGIJSTool.Models
{
    public class Config
    {
        [JsonPropertyName("BGIpath")]
        public string BGIpath { get; set; } = string.Empty;

        [JsonPropertyName("modules")]
        public List<Module> modules { get; set; } = new();
    }

    /// <summary>
    /// 自定义 JSON 转换器：Module.files 既支持旧对象格式 {"bak":[],"del":[],"copy":[]}
    /// 也支持新有序数组格式 [{"op":"bak","paths":[...]}, …]。
    /// 序列化始终输出新格式。
    /// </summary>
    [JsonConverter(typeof(ConfigConverter))]
    public class Module
    {
        [JsonPropertyName("name")]
        public string name { get; set; } = string.Empty;

        /// <summary>有序步骤列表，排列顺序即为执行顺序</summary>
        [JsonPropertyName("files")]
        public List<Step> Steps { get; set; } = new();
    }

    public class Step
    {
        /// <summary>操作类型：bak = 备份, del = 删除, copy = 还原</summary>
        [JsonPropertyName("op")]
        public string op { get; set; } = string.Empty;

        /// <summary>当前操作对应的文件相对路径列表</summary>
        [JsonPropertyName("paths")]
        public List<string> paths { get; set; } = new();
    }

    /// <summary>
    /// 配置反序列化时自动把旧格式（files 为对象 {"bak":[],"del":[],"copy":[]}）转为新的有序 Step 列表。
    /// 序列化始终输出数组格式。
    /// </summary>
    public class ConfigConverter : JsonConverter<Module>
    {
        public override Module Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            var module = new Module();

            if (root.TryGetProperty("name", out var nameEl))
                module.name = nameEl.GetString() ?? string.Empty;

            if (root.TryGetProperty("files", out var filesEl))
            {
                if (filesEl.ValueKind == JsonValueKind.Array)
                {
                    // 新格式：直接反序列化为 Step 列表
                    module.Steps = JsonSerializer.Deserialize<List<Step>>(filesEl.GetRawText(), options)
                                   ?? new List<Step>();
                }
                else if (filesEl.ValueKind == JsonValueKind.Object)
                {
                    // 旧格式兼容：按 bak -> del -> copy 固定顺序转换
                    module.Steps = ConvertLegacyFiles(filesEl);
                }
                // else: ValueKind = Null / Undefined → 空列表
            }

            return module;
        }

        private static List<Step> ConvertLegacyFiles(JsonElement filesObj)
        {
            var steps = new List<Step>();

            if (filesObj.TryGetProperty("bak", out var bakEl) && bakEl.ValueKind == JsonValueKind.Array)
            {
                var paths = new List<string>();
                foreach (var p in bakEl.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.String) paths.Add(p.GetString() ?? string.Empty);
                if (paths.Count > 0) steps.Add(new Step { op = "bak", paths = paths });
            }

            if (filesObj.TryGetProperty("del", out var delEl) && delEl.ValueKind == JsonValueKind.Array)
            {
                var paths = new List<string>();
                foreach (var p in delEl.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.String) paths.Add(p.GetString() ?? string.Empty);
                if (paths.Count > 0) steps.Add(new Step { op = "del", paths = paths });
            }

            if (filesObj.TryGetProperty("copy", out var copyEl) && copyEl.ValueKind == JsonValueKind.Array)
            {
                var paths = new List<string>();
                foreach (var p in copyEl.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.String) paths.Add(p.GetString() ?? string.Empty);
                if (paths.Count > 0) steps.Add(new Step { op = "copy", paths = paths });
            }

            return steps;
        }

        public override void Write(Utf8JsonWriter writer, Module value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("name", value.name);

            writer.WritePropertyName("files");
            writer.WriteStartArray();
            foreach (var step in value.Steps)
            {
                writer.WriteStartObject();
                writer.WriteString("op", step.op);
                writer.WritePropertyName("paths");
                JsonSerializer.Serialize(writer, step.paths, options);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
    }
}
