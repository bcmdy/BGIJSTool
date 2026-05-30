using System.Text;
using BGIJSTool.Services;
using Xunit;

namespace BGIJSTool.Tests;

public sealed class ConfigServiceTests
{
    [Fact]
    public void ValidateConfig_ReturnsErrorForUnknownOperation()
    {
        using var workspace = TestWorkspace.Create();
        var configPath = workspace.WriteFile("config.json", """
        {
          "BGIpath": "D:\\BetterGI",
          "modules": [
            {
              "name": "bad",
              "files": [
                { "op": "unknown", "paths": [ "a.json" ] }
              ]
            }
          ]
        }
        """);

        var service = new ConfigService(configPath);

        var result = service.ValidateConfig();

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, item => item.Contains("unknown op"));
    }

    [Fact]
    public void ValidateConfig_DoesNotWarnWhenBakAndDelSharePath()
    {
        using var workspace = TestWorkspace.Create();
        var configPath = workspace.WriteFile("config.json", """
        {
          "BGIpath": "D:\\BetterGI",
          "modules": [
            {
              "name": "normal",
              "files": [
                { "op": "bak", "paths": [ "a.json" ] },
                { "op": "del", "paths": [ "a.json" ] }
              ]
            }
          ]
        }
        """);

        var service = new ConfigService(configPath);

        var result = service.ValidateConfig();

        Assert.False(result.HasErrors);
        Assert.DoesNotContain(result.Warnings, item => item.Contains("duplicate path"));
    }

    [Fact]
    public void SaveBGIPath_KeepsChineseReadable()
    {
        using var workspace = TestWorkspace.Create();
        var configPath = workspace.WriteFile("config.json", """
        {
          "BGIpath": "",
          "modules": [
            {
              "name": "中文模块",
              "files": []
            }
          ]
        }
        """);

        var service = new ConfigService(configPath);
        service.LoadConfig();
        service.SaveBGIPath(@"D:\BetterGI");

        var saved = File.ReadAllText(configPath, Encoding.UTF8);

        Assert.Contains("中文模块", saved);
        Assert.DoesNotContain(@"\u4E2D", saved, StringComparison.OrdinalIgnoreCase);
    }
}
