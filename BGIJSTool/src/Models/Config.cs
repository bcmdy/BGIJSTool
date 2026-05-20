using System.Collections.Generic;
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

    public class Module
    {
        [JsonPropertyName("name")]
        public string name { get; set; } = string.Empty;
        [JsonPropertyName("files")]
        public Files files { get; set; } = new();
    }

    public class Files
    {
        [JsonPropertyName("bak")]
        public List<string> bak { get; set; } = new();
        [JsonPropertyName("del")]
        public List<string> del { get; set; } = new();
        [JsonPropertyName("copy")]
        public List<string> copy { get; set; } = new();
    }
}
