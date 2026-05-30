using System.Text;

namespace BGIJSTool.Tests;

internal sealed class TestWorkspace : IDisposable
{
    private TestWorkspace(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public static TestWorkspace Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "BGIJSTool.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new TestWorkspace(root);
    }

    public string WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content, Encoding.UTF8);
        return fullPath;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
        }
    }
}
