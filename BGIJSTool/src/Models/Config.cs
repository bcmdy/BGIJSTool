using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BGIJSTool.Models
{
    public enum OpType
    {
        bak,
        del,
        restore,
        copy
    }

    public class Config
    {
        public string BGIpath { get; set; } = string.Empty;
        public List<Module> modules { get; set; } = new();
    }

    public class Module
    {
        public string name { get; set; } = string.Empty;
        public List<Step> Steps { get; set; } = new();
    }

    public class Step
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public OpType op { get; set; }
        public List<string> paths { get; set; } = new();
    }

    /// <summary>
    /// JSON 转换器：将 module.files（数组格式）反序列化为 List&lt;Step&gt;，并序列化输出。
    /// 只支持新有序数组格式 [{op, paths}, …]。
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

            if (root.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
            {
                module.Steps = JsonSerializer.Deserialize<List<Step>>(filesEl.GetRawText(), options)
                               ?? new List<Step>();
            }

            return module;
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
                writer.WriteString("op", step.op.ToString());
                writer.WritePropertyName("paths");
                JsonSerializer.Serialize(writer, step.paths, options);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }
}
