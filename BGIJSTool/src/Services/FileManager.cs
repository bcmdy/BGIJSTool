using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using BGIJSTool.Models;
using E = System.Text.Encoding;

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

    /// <summary>按模块步骤依次执行（bak+del 先备份再删；然后 restore → copy）</summary>
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
        }

        // 先备份（在删除之前，确保源文件还在）
        if (hasBak && bakPaths.Count > 0)
        {
            var zipLabel = "backup";
            try { zipLabel = moduleSteps.First().paths.FirstOrDefault() ?? "backup"; } catch { }
            CreateBakZip(bakPaths.ToList(), zipLabel, logger);
        }

        // 再执行具体操作
        foreach (var step in moduleSteps)
        {
            switch (step.op)
            {
                case OpType.bak: break; // 已提前处理
                case OpType.del: ExecuteDel(step.paths, logger); break;
                case OpType.restore:
                    foreach (var p in step.paths)
                        foreach (var resolved in ResolveBgi(p))
                            RestoreFile(resolved, logger);
                    break;
                case OpType.copy: ExecuteCopy(step, logger); break;
            }
        }
    }

    /// <summary>合调单步的便利入口</summary>
    public void ExecuteStep(Step step, ILogger logger)
        => ExecuteSteps(new[] { step }, logger);

    // =========================================================================
    //  bak: zip 打包 + 写 restore 清单
    // =========================================================================

    public void CreateBakZip(List<string> allPaths, string zipName, ILogger logger)
    {
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
                var manifest = JsonSerializer.Deserialize<RestoreManifest>(
                    File.ReadAllText(manifestFile));

                if (manifest?.RestoreEntries is { Count: > 0 } entries)
                {
                    logger.LogInfo($"还原清单: {manifest.ModuleName}  (创建于 {manifest.CreatedAt})");
                    foreach (var entry in entries)
                    {
                        if (entry.Op.StartsWith("bak+del"))
                        {
                            foreach (var rp in entry.SrcPaths)
                            {
                                // 直接从备份 zip 临时目录复制，不走单文件 backup/ 路径
                                var src = Path.Combine(tmpDir, rp.Replace('/', Path.DirectorySeparatorChar));
                                var dst = GetFullPath(rp);
                                if (!File.Exists(src))
                                {
                                    logger.LogError($"备份文件在 zip 中不存在: {rp}");
                                    continue;
                                }
                                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                                File.Copy(src, dst, true);
                                logger.LogSuccess($"还原: {rp} -> {dst}");
                            }
                        }
                    }
                    try { Directory.Delete(tmpDir, true); } catch { }
                    return;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"读取还原清单失败: {ex.Message}，将尝试全量还原");
            }
        }

        // Fallback: 还原 zip 内所有非 manifest 文件
        foreach (var f in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains("_restore_manifest.json", StringComparison.OrdinalIgnoreCase)))
        {
            var rel = MakeRelFromRoot(f, tmpDir);
            var dst = GetFullPath(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, true);
            logger.LogSuccess($"还原: {rel} -> {dst}");
        }
        try { Directory.Delete(tmpDir, true); } catch { }
    }

    /// <summary>
    /// 基于指定根目录计算相对路径（替代 Substring 方案，避免路径分隔符问题）。
    /// </summary>
    private static string MakeRelFromRoot(string fullPath, string rootDir)
    {
        var prefix = rootDir.TrimEnd('\\') + "\\";
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return fullPath;
        return fullPath.Substring(prefix.Length).Replace('\\', '/');
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

        try
        {
            int copied = ExtractZipWithEncoding(zipFile, GetFullPath(""), logger);
            logger.LogInfo($"copy 完成，共还原 {copied} 个文件");
        }
        catch (Exception ex)
        {
            logger.LogError($"解压失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 用 ZipArchive 逐条读取，通过启发式检测自动识别 GBK/UTF-8 编码。
    /// zip 条目名本身无法存储 ANSI/GBK 标志（除非用非标准 APPNote 字段），
    /// 这里用 "UTF-8 解码结果含乱码 → 用 GBK 重解码" 策略处理。
    /// </summary>
    private int ExtractZipWithEncoding(string zipFile, string destDir, ILogger logger)
    {
        int copied = 0;
        var gb18030 = E.GetEncoding("GB18030"); // 兼容 GBK / GB2312
        var gbk     = E.GetEncoding("GBK");

        using var fs = File.OpenRead(zipFile);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            // 跳过纯目录条目（FullName 以 / 结尾且 Name 为空）
            if (entry.FullName.EndsWith("/") && string.IsNullOrEmpty(entry.Name))
                continue;

            var decodedName = AutoDecodeZipName(entry.FullName, gb18030, gbk);
            var targetPath  = Path.Combine(destDir, decodedName);
            var targetDir   = Path.GetDirectoryName(targetPath)!;

            Directory.CreateDirectory(targetDir);
            entry.ExtractToFile(targetPath, true);
            copied++;
            logger.LogSuccess($"copy 解压覆盖: {decodedName} -> {targetPath}");
        }

        return copied;
    }

    /// <summary>
    /// 启发式检测：先检查 UTF-8 解码是否已正确；若含乱码则逆推原始字节
    /// 并用 GB18030 / GBK 重解码，取第一个无乱码结果。
    /// </summary>
    private static string AutoDecodeZipName(string utf8Name, Encoding gb18030, Encoding gbk)
    {
        if (!ContainsMojibake(utf8Name))
            return utf8Name.Replace('/', '\\');

        // 逆推：用 UTF-8 字节序列尝试用 GB18030 解码
        try
        {
            var bytes    = E.UTF8.GetBytes(utf8Name);
            var gbResult = gb18030.GetString(bytes);
            if (!ContainsMojibake(gbResult))
                return gbResult.Replace('/', '\\');
        }
        catch { }

        // 再试 GBK
        try
        {
            var bytes    = E.UTF8.GetBytes(utf8Name);
            var gbResult = gbk.GetString(bytes);
            if (!ContainsMojibake(gbResult))
                return gbResult.Replace('/', '\\');
        }
        catch { }

        // 都失败了，返回原始 UTF-8 解码结果
        return utf8Name.Replace('/', '\\');
    }

    /// <summary>
    /// 检查字符串是否含乱码特征（替换字符 "、锟斤拷 类、不可见控制字符）。
    /// </summary>
    private static bool ContainsMojibake(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Contains('�')) return true;           // Unicode 替换字符
        if (text.Contains('锟')) return true;               // 锟斤拷 / 锟芥补

        foreach (var c in text)
        {
            if (c < 32 && c is not ('\r' or '\n' or '\t'))
                return true;
        }
        return false;
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
