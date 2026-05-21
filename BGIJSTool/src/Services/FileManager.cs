using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using BGIJSTool.Models;

namespace BGIJSTool.Services;

public class FileManager
{
    private readonly string _bgiPath;
    private readonly string _programPath;
    private readonly string _backupPath;
    private readonly string _copyPath;

    private record RestoreEntry(
        [property: JsonPropertyName("op")] string Op,
        [property: JsonPropertyName("srcPaths")] List<string> SrcPaths);

    private record RestoreManifest(
        [property: JsonPropertyName("moduleName")] string ModuleName,
        [property: JsonPropertyName("createdAt")] string CreatedAt,
        [property: JsonPropertyName("restore")] List<RestoreEntry> RestoreEntries);

    public FileManager(string bgiPath, string programPath)
    {
        _bgiPath = bgiPath;
        _programPath = programPath;
        _backupPath = Path.Combine(programPath, "backup");
        Directory.CreateDirectory(_backupPath);
        _copyPath = Path.Combine(programPath, "copy");
        Directory.CreateDirectory(_copyPath);
    }

    public string GetFullPath(string relativePath)
        => Path.Combine(_bgiPath, "User", "JsScript", relativePath);

    public string GetBackupPath(string relativePath)
        => Path.Combine(_backupPath, relativePath);

    public string GetCopySourcePath(string relativePath)
        => Path.Combine(_copyPath, relativePath);

    // =========================================================================
    //  对外入口（供 MainWindow 调用）
    // =========================================================================

    /// <summary>按模块步骤依次执行（bak → del → restore → copy）</summary>
    public void ExecuteSteps(IEnumerable<Step> steps, ILogger logger)
    {
        var moduleSteps = steps.ToList();
        if (moduleSteps.Count == 0) return;

        bool hasBak = false;
        var bakPaths = new HashSet<string>();

        foreach (var step in moduleSteps)
        {
            if (step.op is OpType.bak or OpType.del)
                foreach (var p in step.paths) bakPaths.Add(p);
            if (step.op == OpType.bak) hasBak = true;

            switch (step.op)
            {
                case OpType.bak: break;
                case OpType.del: ExecuteDel(step.paths, logger); break;
                case OpType.restore:
                    foreach (var p in step.paths)
                        foreach (var resolved in ResolveBgi(p))
                            RestoreFile(resolved, logger);
                    break;
                case OpType.copy: ExecuteCopy(step, logger); break;
            }
        }

        if (hasBak && bakPaths.Count > 0)
        {
            CreateBakZip(bakPaths.ToList(), _ => "backup", logger);
        }
    }

    /// <summary>合调单步的便利入口</summary>
    public void ExecuteStep(Step step, ILogger logger)
        => ExecuteSteps(new[] { step }, logger);

    // =========================================================================
    //  bak: zip 打包 + 写 restore 清单
    // =========================================================================

    public void CreateBakZip(List<string> allPaths, Func<Step, string> moduleNameGetter, ILogger logger)
    {
        var zipName = moduleNameGetter.Invoke(new Step());
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var zipFn = string.IsNullOrEmpty(zipName) ? $"backup_{ts}.zip" : $"{zipName}_{ts}.zip";
        var zipFull = Path.Combine(_backupPath, zipFn);

        var tmpDir = Path.Combine(_backupPath, $"_tmp_bak_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var baked = new List<string>();
        foreach (var path in allPaths)
        {
            foreach (var resolved in ResolveBgi(path))
            {
                var src = GetFullPath(resolved);
                if (!File.Exists(src)) continue;
                var dest = Path.Combine(tmpDir, resolved.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, true);
                baked.Add(resolved);
            }
        }

        if (baked.Count > 0)
        {
            var manifest = new
            {
                moduleName = zipName,
                createdAt = DateTime.Now.ToString("yyyyMMddTHHmmss"),
                restore = new[]
                {
                    new { op = "bak+del", srcPaths = baked.ToList() }
                }
            };
            File.WriteAllText(
                Path.Combine(tmpDir, "_restore_manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }

        System.IO.Compression.ZipFile.CreateFromDirectory(tmpDir, zipFull,
            System.IO.Compression.CompressionLevel.Optimal, false);
        try { Directory.Delete(tmpDir, true); } catch { }

        if (baked.Count > 0)
            logger.LogSuccess($"备份完成（zip）: {zipFn}  共 {baked.Count} 个文件");
        else
            logger.LogWarning($"bak 路径未能解析到实际文件，未生成 zip: {zipFn}");
    }

    // =========================================================================
    //  del
    // =========================================================================

    private void ExecuteDel(IEnumerable<string> paths, ILogger logger)
    {
        foreach (var p in paths)
            foreach (var resolved in ResolveBgi(p))
                DeleteFile(resolved, logger);
    }

    // =========================================================================
    //  restore
    // =========================================================================

    public void ExecuteRestoreFromZip(string zipFileName, ILogger logger)
    {
        var zipFull = Path.Combine(_backupPath, zipFileName);
        if (!File.Exists(zipFull))
        {
            logger.LogError($"备份 zip 不存在: {zipFull}");
            return;
        }

        var tmpDir = Path.Combine(_backupPath, $"_restore_tmp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        System.IO.Compression.ZipFile.ExtractToDirectory(zipFull, tmpDir);

        var manifestFile = Path.Combine(tmpDir, "_restore_manifest.json");
        if (File.Exists(manifestFile))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<RestoreManifest>(File.ReadAllText(manifestFile));
                if (manifest != null && manifest.RestoreEntries.Count > 0)
                {
                    logger.LogInfo($"还原清单: {manifest.ModuleName}  (创建于 {manifest.CreatedAt})");
                    foreach (var entry in manifest.RestoreEntries)
                    {
                        if (entry.Op.StartsWith("bak+del"))
                            foreach (var rp in entry.SrcPaths)
                                RestoreFile(rp, logger);
                    }
                    try { Directory.Delete(tmpDir, true); } catch { }
                    return;
                }
            }
            catch { /* fallback */ }
        }

        // Fallback: 还原 zip 内所有非 manifest 文件
        foreach (var f in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains("_restore_manifest.json", StringComparison.OrdinalIgnoreCase)))
        {
            var rel = f.Substring(tmpDir.Length).Replace('\\', '/').TrimStart('/');
            RestoreFile(rel, logger);
        }
        try { Directory.Delete(tmpDir, true); } catch { }
    }

    // =========================================================================
    //  copy — zip 解压覆盖模式
    // =========================================================================

    public void ExecuteCopy(Step step, ILogger logger)
    {
        // copy 的 paths[0] = zip 包名（如 "狗粮批发线路收尾改茶叶【AB都改】.zip"）
        var zipQuery = step.paths.Count > 0 ? step.paths[0] : "*.zip";
        var matched  = FindCopyZip(zipQuery);
        if (matched.Count == 0)
        {
            logger.LogWarning($"copy/ 目录下未找到匹配的 zip: {zipQuery}，跳过本步骤");
            return;
        }

        var zipFile = matched[0];
        logger.LogInfo($"解压 copy 压缩包: {Path.GetFileName(zipFile)}");

        var tmpDir = Path.Combine(_backupPath, $"_copy_extract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(zipFile, tmpDir);
            int copied = CopyTreeOverwrite(tmpDir, GetFullPath(""), logger);
            logger.LogInfo($"copy 完成，共还原 {copied} 个文件");
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    private List<string> FindCopyZip(string query)
    {
        // query 可以是文件名（精确）、*.zip（通配）或空（fallback 全部）
        if (string.IsNullOrWhiteSpace(query))
            return Directory.GetFiles(_copyPath, "*.zip", SearchOption.TopDirectoryOnly).ToList();

        // 精确匹配
        var full = Path.Combine(_copyPath, query);
        if (File.Exists(full)) return new List<string> { full };

        // 通配符匹配 *.zip
        if (query.Contains('*'))
            return Directory.GetFiles(_copyPath, query, SearchOption.TopDirectoryOnly).ToList();

        // 前缀匹配 *.zip
        return Directory.GetFiles(_copyPath, $"{query}*.zip", SearchOption.TopDirectoryOnly)
                        .ToList();
    }

    // 旧版兼容
    public void ExecuteCopyStep(Step step, ILogger logger) => ExecuteCopy(step, logger);

    // =========================================================================
    //  基础文件操作（保持 API 不变）
    // =========================================================================

    public void BackupFile(string relativePath, ILogger logger)
    {
        var src = GetFullPath(relativePath);
        var dst = GetBackupPath(relativePath);
        if (!File.Exists(src))
        {
            logger.LogWarning($"文件不存在，跳过备份: {src}");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, true);
        logger.LogSuccess($"{src} -> {dst}");
    }

    public void DeleteFile(string relativePath, ILogger logger)
    {
        var full = GetFullPath(relativePath);
        if (!File.Exists(full))
        {
            logger.LogWarning($"文件不存在，跳过删除: {full}");
            return;
        }
        File.Delete(full);
        logger.LogSuccess(full);
    }

    public void RestoreFile(string relativePath, ILogger logger)
    {
        var src = GetBackupPath(relativePath);
        var dst = GetFullPath(relativePath);
        if (!File.Exists(src))
        {
            logger.LogError($"备份文件不存在，无法还原: {src}");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, true);
        logger.LogSuccess($"{src} -> {dst}");
    }

    // =========================================================================
    //  私有辅助
    // =========================================================================

    private IEnumerable<string> ResolveBgi(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');

        if (path.Contains('*'))
        {
            var baseDir = Path.GetDirectoryName(GetFullPath(trimmed)) ?? GetFullPath("");
            var pattern = Path.GetFileName(trimmed);
            if (!Directory.Exists(baseDir)) yield break;
            foreach (var f in Directory.GetFiles(baseDir, pattern, SearchOption.TopDirectoryOnly))
                yield return MakeRel(f);
            yield break;
        }

        if (path.EndsWith("/") || path.EndsWith("\\")
            || (Directory.Exists(GetFullPath(path)) && !File.Exists(GetFullPath(trimmed))))
        {
            var dir = GetFullPath(trimmed);
            if (!Directory.Exists(dir)) yield break;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                yield return MakeRel(f);
            yield break;
        }

        yield return path;
    }

    /// <summary>
    /// 将绝对路径转为 JsScript 根目录下的相对路径；
    /// 用完整前缀（含尾部 \）匹配，零字符偏移误差，中文路径不截断。
    /// </summary>
    private string MakeRel(string fullPath)
    {
        // GetFullPath("") = E:\...\BetterGI\User\JsScript  末尾不加 \；
        // 构造带尾部分隔符的前缀，IndexOf 精确找到前缀结束位置
        var prefix = GetFullPath("") + "\\";   // E:\...\JsScript\
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return fullPath;
        return fullPath.Substring(prefix.Length).Replace('\\', '/');
    }

    private int CopyTreeOverwrite(string srcDir, string dstDir, ILogger? logger = null)
    {
        int n = 0;
        foreach (var file in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcDir, file);
            var dest = Path.Combine(dstDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
            n++;
            logger?.LogSuccess($"copy 解压覆盖: {file} -> {dest}");
        }
        return n;
    }
}
