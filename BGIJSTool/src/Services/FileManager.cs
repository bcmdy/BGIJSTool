using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Linq;
using BGIJSTool.Models;
using ICSharpCode.SharpZipLib.Zip;

namespace BGIJSTool.Services;

public class FileManager
{
    private static readonly Encoding GBK     = Encoding.GetEncoding("GBK");
    private static readonly Encoding GB18030 = Encoding.GetEncoding("GB18030");

    private readonly string _bgiPath;
    private readonly string _programPath;
    private readonly string _backupPath;
    private readonly string _copyPath;

    private record RestoreEntry(
        [property: JsonPropertyName("op")] string Op,
        [property: JsonPropertyName("srcPaths")] List<string>? SrcPaths = null,
        [property: JsonPropertyName("paths")] List<string>? Paths = null);

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
    //  对外入口
    // =========================================================================

    public void ExecuteSteps(IEnumerable<Step> steps, ILogger logger)
    {
        var moduleSteps = steps.ToList();
        if (moduleSteps.Count == 0) return;

        bool hasBak = false;
        var bakPaths = new HashSet<string>();
        var copyPaths = new List<string>();

        foreach (var step in moduleSteps)
        {
            if (step.op is OpType.bak or OpType.del)
                foreach (var p in step.paths) bakPaths.Add(p);
            if (step.op == OpType.bak) hasBak = true;
            if (step.op == OpType.copy) copyPaths.AddRange(step.paths);
        }

        if (hasBak && bakPaths.Count > 0)
        {
            var zipLabel = "backup";
            try { zipLabel = moduleSteps.First().paths.FirstOrDefault() ?? "backup"; } catch { }
            CreateBakZip(bakPaths.ToList(), copyPaths, zipLabel, logger);
        }

        foreach (var step in moduleSteps)
        {
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
    }

    public void ExecuteStep(Step step, ILogger logger)
        => ExecuteSteps(new[] { step }, logger);

    // =========================================================================
    //  bak
    // =========================================================================

    public void CreateBakZip(List<string> allPaths, List<string> copyPaths, string zipName, ILogger logger)
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

        // 收集 copy zip 中的文件列表（用于 restore 时清理）
        var copyDeletedPaths = new List<string>();
        foreach (var cp in copyPaths)
        {
            var zipFile = Path.Combine(_copyPath, cp);
            if (!File.Exists(zipFile)) continue;

            try
            {
                using var fs = File.OpenRead(zipFile);
                using var zf = new ICSharpCode.SharpZipLib.Zip.ZipFile(fs);
                foreach (ZipEntry entry in zf)
                {
                    if (!entry.IsDirectory)
                        copyDeletedPaths.Add(entry.Name.Replace('\\', '/'));
                }
            }
            catch { }
        }

        var restoreEntries = new List<object>();

        if (copyDeletedPaths.Count > 0)
        {
            restoreEntries.Add(new { op = "del", paths = copyDeletedPaths });
        }

        if (baked.Count > 0)
        {
            restoreEntries.Add(new { op = "restore", srcPaths = baked.ToList() });
        }

        if (restoreEntries.Count > 0)
        {
            var manifest = new
            {
                moduleName = zipName,
                createdAt = DateTime.Now.ToString("yyyyMMddTHHmmss"),
                restore = restoreEntries
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
                        if (entry.Op == "del")
                        {
                            foreach (var p in entry.Paths ?? new List<string>())
                            {
                                DeleteFile(p, logger);
                            }
                        }
                        else if (entry.Op == "restore")
                        {
                            foreach (var rp in entry.SrcPaths ?? new List<string>())
                            {
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

    private static string MakeRelFromRoot(string fullPath, string rootDir)
    {
        var prefix = rootDir.TrimEnd('\\') + "\\";
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return fullPath;
        return fullPath.Substring(prefix.Length).Replace('\\', '/');
    }

    // =========================================================================
    //  copy — SharpZipLib + 自动编码识别
    // =========================================================================

    public void ExecuteCopy(Step step, ILogger logger)
    {
        var zipQuery = step.paths.Count > 0 ? step.paths[0] : "*.zip";
        var matched = FindCopyZip(zipQuery);
        if (matched.Count == 0)
        {
            logger.LogWarning($"copy/ 目录下未找到匹配的 zip: {zipQuery}，跳过本步骤");
            return;
        }

        var zipFile = matched[0];
        logger.LogInfo($"解压 copy 压缩包: {Path.GetFileName(zipFile)}");

        try
        {
            int copied = ExtractWithAutoEncoding(zipFile, GetFullPath(""), logger);
            logger.LogInfo($"copy 完成，共还原 {copied} 个文件");
        }
        catch (Exception ex)
        {
            logger.LogError($"解压失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// SharpZipLib 解压，自动识别条目名编码（UTF-8 vs GBK）。
    /// 通过启发式检测判断 SharpZipLib 解码后的文件名是否正确。
    /// </summary>
    private int ExtractWithAutoEncoding(string zipFile, string destDir, ILogger logger)
    {
        int copied = 0;

        using var fs = File.OpenRead(zipFile);
        using var zf = new ICSharpCode.SharpZipLib.Zip.ZipFile(fs);

        // 先尝试用 UTF-8 读取所有条目，检测是否有乱码
        var entries = new List<(ZipEntry Entry, string DecodedName, bool IsValid)>();
        foreach (ZipEntry entry in zf)
        {
            if (entry.IsDirectory) continue;

            var decodedName = entry.Name; // SharpZipLib 自动解码的结果
            var isValid = !ContainsMojibake(decodedName) && IsPlausiblePath(decodedName);
            entries.Add((entry, decodedName, isValid));
        }

        // 判断整体编码：如果有超过 50% 的条目乱码，则认为是 GBK 编码
        var total = entries.Count;
        var validCount = entries.Count(e => e.IsValid);
        var useGbk = total > 0 && validCount < total / 2;

        if (useGbk)
            logger.LogInfo("检测到 zip 使用 GBK 编码，将重新解码文件名");

        foreach (var (entry, decodedName, isValid) in entries)
        {
            string entryName;
            if (isValid || !useGbk)
            {
                entryName = decodedName;
            }
            else
            {
                // 用 GBK 重新解码原始字节
                entryName = ReDecodeWithGbk(decodedName);
                logger.LogInfo($"编码修正: {decodedName} -> {entryName}");
            }

            var targetPath = Path.Combine(destDir, entryName.Replace('/', '\\'));
            var targetDir = Path.GetDirectoryName(targetPath)!;

            Directory.CreateDirectory(targetDir);

            using var entryStream = zf.GetInputStream(entry);
            using var fileStream = File.Create(targetPath);
            entryStream.CopyTo(fileStream);

            copied++;
            logger.LogSuccess($"copy 解压覆盖: {entryName} -> {targetPath}");
        }

        return copied;
    }

    /// <summary>
    /// 将 SharpZipLib 用 UTF-8 解码后的乱码字符串，用 GBK 重新解码原始字节。
    /// 原理：UTF-8 解码 GBK 字节后得到的乱码字符串，再 Encode 回 UTF-8 字节，
    /// 这些字节其实就是原始的 GBK 字节，再用 GBK 解码即可。
    /// </summary>
    private static string ReDecodeWithGbk(string utf8DecodedGarbage)
    {
        try
        {
            // 关键步骤：把 UTF-8 乱码字符串编码回字节（这些字节就是原始 GBK 字节）
            var bytes = Encoding.UTF8.GetBytes(utf8DecodedGarbage);
            var fixedName = GBK.GetString(bytes);

            if (!ContainsMojibake(fixedName))
                return fixedName;
        }
        catch { }

        try
        {
            // 再试 GB18030
            var bytes = Encoding.UTF8.GetBytes(utf8DecodedGarbage);
            var fixedName = GB18030.GetString(bytes);

            if (!ContainsMojibake(fixedName))
                return fixedName;
        }
        catch { }

        return utf8DecodedGarbage;
    }

    /// <summary>
    /// 启发式检测：判断字符串是否含乱码。
    /// 检测 Unicode 替换字符、锟斤拷模式、以及中文路径中的异常字符。
    /// </summary>
    private static bool ContainsMojibake(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        // 1. Unicode 替换字符 U+FFFD
        if (text.Contains('�')) return true;

        // 2. 常见乱码模式（锟斤拷系列）
        if (text.Contains('锟')) return true;

        // 3. 检测是否有大量希腊字母/西里尔字母等（UTF-8 多字节被拆散后的常见特征）
        //    比如 "β·" 这种就是典型的 UTF-8 字节被当成 Latin-1 后的结果
        int suspiciousCount = 0;
        foreach (var c in text)
        {
            // 希腊字母范围 U+0370-U+03FF
            if (c >= 'Ͱ' && c <= 'Ͽ') suspiciousCount++;
            // 西里尔字母范围 U+0400-U+04FF
            if (c >= 'Ѐ' && c <= 'ӿ') suspiciousCount++;
            // 控制字符（除正常换行制表）
            if (c < 32 && c is not ('\r' or '\n' or '\t')) return true;
        }

        // 如果希腊/西里尔字母占比过高，认为是乱码
        if (text.Length > 0 && suspiciousCount > text.Length / 4) return true;

        return false;
    }

    /// <summary>
    /// 判断路径是否像合理的中文路径（包含常见中文字符或特定英文关键词）。
    /// 用于区分 "确实乱码" 和 "本来就是英文路径" 的情况。
    /// </summary>
    private static bool IsPlausiblePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;

        // 如果路径包含这些中文游戏术语，认为是中文路径
        var chineseMarkers = new[]
        {
            "路线", "执行", "准备", "清怪", "收尾",
            "稻妻", "须弥", "踏鞴", "智障", "狗粮", "茶叶",
            "ArtifactsPath"
        };
        foreach (var marker in chineseMarkers)
        {
            if (path.Contains(marker)) return true;
        }

        // 如果路径全是 ASCII，也认为是合理的（纯英文路径）
        if (path.All(c => c < 128)) return true;

        // 包含非 ASCII 但不含中文标记，可能是乱码
        return false;
    }

    // =========================================================================
    //  其他方法保持不变
    // =========================================================================

    private List<string> FindCopyZip(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Directory.GetFiles(_copyPath, "*.zip", SearchOption.TopDirectoryOnly).ToList();

        var full = Path.Combine(_copyPath, query);
        if (File.Exists(full)) return new List<string> { full };

        if (query.Contains('*'))
            return Directory.GetFiles(_copyPath, query, SearchOption.TopDirectoryOnly).ToList();

        return Directory.GetFiles(_copyPath, $"{query}*.zip", SearchOption.TopDirectoryOnly).ToList();
    }

    public void ExecuteCopyStep(Step step, ILogger logger) => ExecuteCopy(step, logger);

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

        // 清理空文件夹
        var dir = Path.GetDirectoryName(full);
        while (!string.IsNullOrEmpty(dir) && dir != GetFullPath(""))
        {
            try
            {
                if (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
                    Directory.Delete(dir);
                else
                    break;
            }
            catch
            {
                break;
            }
            dir = Path.GetDirectoryName(dir);
        }
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

    private string MakeRel(string fullPath)
    {
        var prefix = GetFullPath("") + "\\";
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return fullPath;
        return fullPath.Substring(prefix.Length).Replace('\\', '/');
    }
}
