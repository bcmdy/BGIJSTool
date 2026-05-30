using System.IO.Compression;
using System.Text;
using BGIJSTool.Services;
using Xunit;

namespace BGIJSTool.Tests;

public sealed class FileManagerTests
{
    [Theory]
    [InlineData("bad:name?.zip", "bad_name_.zip")]
    [InlineData("  module.  ", "module")]
    public void SanitizeFileName_ReplacesInvalidCharacters(string input, string expected)
    {
        Assert.Equal(expected, FileManager.SanitizeFileName(input));
    }

    [Fact]
    public void GetBackupInfos_ReadsRestoreManifestSummary()
    {
        using var workspace = TestWorkspace.Create();
        var backupDir = Path.Combine(workspace.Root, "backup");
        Directory.CreateDirectory(backupDir);
        var zipPath = Path.Combine(backupDir, "20260531_120000_test.zip");

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifestEntry = archive.CreateEntry("_restore_manifest.json");
            using var stream = manifestEntry.Open();
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write("""
            {
              "moduleName": "测试模块",
              "createdAt": "20260531T120000",
              "restore": [
                { "op": "del", "paths": [ "new.json" ] },
                { "op": "restore", "srcPaths": [ "old1.json", "old2.json" ] }
              ]
            }
            """);
        }

        var manager = new FileManager(Path.Combine(workspace.Root, "bgi"), workspace.Root);

        var backup = Assert.Single(manager.GetBackupInfos());

        Assert.Equal("测试模块", backup.ModuleName);
        Assert.Equal(2, backup.RestoreFileCount);
        Assert.True(backup.HasCopyCleanup);
        Assert.Contains("测试模块", backup.DisplayText);
    }
}
