using System.Linq;
using System.Text;
using BGIJSTool.Services;
using ICSharpCode.SharpZipLib.Zip;
using Xunit;

namespace BGIJSTool.Tests;

public sealed class FileManagerResolveTests
{
    private static string JsScriptRoot(TestWorkspace ws)
        => Path.Combine(ws.Root, "bgi", "User", "JsScript");

    private static FileManager NewManager(TestWorkspace ws)
        => new(Path.Combine(ws.Root, "bgi"), ws.Root);

    [Fact]
    public void ResolveBgiPaths_ExactFile_NormalizesToForwardSlash()
    {
        using var ws = TestWorkspace.Create();
        var manager = NewManager(ws);

        var resolved = manager.ResolveBgiPaths(@"Example\script.json").ToList();

        Assert.Equal(new[] { "Example/script.json" }, resolved);
    }

    [Fact]
    public void ResolveBgiPaths_Wildcard_ExpandsMatchingFilesInSameDir()
    {
        using var ws = TestWorkspace.Create();
        var root = JsScriptRoot(ws);
        Directory.CreateDirectory(Path.Combine(root, "Example"));
        File.WriteAllText(Path.Combine(root, "Example", "a.json"), "{}");
        File.WriteAllText(Path.Combine(root, "Example", "b.json"), "{}");
        File.WriteAllText(Path.Combine(root, "Example", "c.txt"), "x");

        var manager = NewManager(ws);
        var resolved = manager.ResolveBgiPaths(@"Example\*.json").OrderBy(p => p).ToList();

        Assert.Equal(new[] { "Example/a.json", "Example/b.json" }, resolved);
    }

    [Fact]
    public void ResolveBgiPaths_Directory_ExpandsRecursively()
    {
        using var ws = TestWorkspace.Create();
        var root = JsScriptRoot(ws);
        Directory.CreateDirectory(Path.Combine(root, "Example", "sub"));
        File.WriteAllText(Path.Combine(root, "Example", "a.json"), "{}");
        File.WriteAllText(Path.Combine(root, "Example", "sub", "d.json"), "{}");

        var manager = NewManager(ws);
        var resolved = manager.ResolveBgiPaths(@"Example\").OrderBy(p => p).ToList();

        Assert.Equal(new[] { "Example/a.json", "Example/sub/d.json" }, resolved);
    }

    [Fact]
    public void ResolveBgiPaths_NonexistentWildcard_ReturnsEmpty()
    {
        using var ws = TestWorkspace.Create();
        var manager = NewManager(ws);

        var resolved = manager.ResolveBgiPaths(@"Nope\*.json").ToList();

        Assert.Empty(resolved);
    }

    [Fact]
    public void ExecuteCopy_SkipsPathTraversalEntry_ExtractsSafeEntry()
    {
        using var ws = TestWorkspace.Create();
        var copyDir = Path.Combine(ws.Root, "copy");
        Directory.CreateDirectory(copyDir);
        CreateZip(Path.Combine(copyDir, "evil.zip"), Encoding.UTF8,
            ("../escaped.txt", "evil"),
            ("good/inside.txt", "ok"));

        var manager = NewManager(ws);
        var logger = new FakeLogger();
        manager.ExecuteCopy(new[] { "evil.zip" }, logger);

        var root = JsScriptRoot(ws);
        Assert.True(File.Exists(Path.Combine(root, "good", "inside.txt")));
        // 穿越目标应位于 JsScript 之外（bgi/User/escaped.txt），必须未被写入
        Assert.False(File.Exists(Path.Combine(ws.Root, "bgi", "User", "escaped.txt")));
        Assert.Contains(logger.Messages, m => m.Contains("路径穿越") || m.Contains("非法路径"));
    }

    [Fact]
    public void ExecuteCopy_DecodesGbkEntryName()
    {
        using var ws = TestWorkspace.Create();
        var copyDir = Path.Combine(ws.Root, "copy");
        Directory.CreateDirectory(copyDir);

        // 以 GBK(936) 写入中文条目名，且不置 UTF-8 标志位，模拟中文 Windows 创建的 zip
        const string chineseName = "路线/狗粮.json";
        CreateZip(Path.Combine(copyDir, "gbk.zip"), Encoding.GetEncoding("GBK"),
            (chineseName, "{}"));

        var manager = NewManager(ws);
        manager.ExecuteCopy(new[] { "gbk.zip" }, new FakeLogger());

        var expected = Path.Combine(JsScriptRoot(ws), "路线", "狗粮.json");
        Assert.True(File.Exists(expected), $"未按 GBK 正确解码中文文件名，期望存在: {expected}");
    }

    /// <summary>用指定编码写入 zip 条目名（非 UTF-8 编码时不置语言编码标志位）。</summary>
    private static void CreateZip(string zipPath, Encoding nameEncoding, params (string Name, string Content)[] entries)
    {
        var codec = StringCodec.FromEncoding(nameEncoding);
        using var fs = File.Create(zipPath);
        using var zos = new ZipOutputStream(fs, codec) { IsStreamOwner = true };
        foreach (var (name, content) in entries)
        {
            zos.PutNextEntry(new ZipEntry(name));
            var bytes = Encoding.UTF8.GetBytes(content);
            zos.Write(bytes, 0, bytes.Length);
            zos.CloseEntry();
        }
    }
}
