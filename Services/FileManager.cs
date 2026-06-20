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
    private static readonly Encoding GBK;

    static FileManager()
    {
        // 自行注册代码页提供程序，使 GBK 在未经 App 初始化的场景（如单元测试）下也可用。
        // 必须在 GetEncoding 之前注册，故放在静态构造函数体内而非字段初始化器。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        GBK = Encoding.GetEncoding("GBK");
    }

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

    public List<BackupInfo> GetBackupInfos()
    {
        if (!Directory.Exists(_backupPath))
            return new List<BackupInfo>();

        return Directory.GetFiles(_backupPath, "*.zip", SearchOption.TopDirectoryOnly)
            .Select(ReadBackupInfoCached)
            .OrderByDescending(info => info.CreatedAt ?? File.GetLastWriteTime(info.FullPath))
            .ToList();
    }

    // 按“完整路径 + 最后写入时间”缓存已解析的备份信息，避免每次刷新列表都重新
    // 打开所有 zip 读取清单。FileManager 每次操作都会重建，故缓存设为静态共享。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime LastWrite, BackupInfo Info)>
        _backupInfoCache = new(StringComparer.OrdinalIgnoreCase);

    private BackupInfo ReadBackupInfoCached(string zipFullPath)
    {
        var lastWrite = File.GetLastWriteTimeUtc(zipFullPath);
        if (_backupInfoCache.TryGetValue(zipFullPath, out var cached) && cached.LastWrite == lastWrite)
            return cached.Info;

        var info = ReadBackupInfo(zipFullPath);
        _backupInfoCache[zipFullPath] = (lastWrite, info);
        return info;
    }

    private BackupInfo ReadBackupInfo(string zipFullPath)
    {
        var fileName = Path.GetFileName(zipFullPath);
        var fallbackTime = File.GetLastWriteTime(zipFullPath);

        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(zipFullPath);
            var entry = archive.GetEntry("_restore_manifest.json");
            if (entry is null)
                return BackupInfo.FromFile(fileName, zipFullPath, fallbackTime);

            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize<RestoreManifest>(stream);
            if (manifest is null)
                return BackupInfo.FromFile(fileName, zipFullPath, fallbackTime);

            var restoreCount = manifest.RestoreEntries
                .Where(item => item.Op == "restore")
                .Sum(item => item.SrcPaths?.Count ?? 0);
            var deleteCount = manifest.RestoreEntries
                .Where(item => item.Op == "del")
                .Sum(item => item.Paths?.Count ?? 0);
            var createdAt = ParseManifestTime(manifest.CreatedAt) ?? fallbackTime;

            return new BackupInfo(
                fileName,
                zipFullPath,
                manifest.ModuleName,
                createdAt,
                restoreCount,
                deleteCount,
                deleteCount > 0);
        }
        catch
        {
            return BackupInfo.FromFile(fileName, zipFullPath, fallbackTime);
        }
    }

    private static DateTime? ParseManifestTime(string value)
    {
        if (DateTime.TryParseExact(value, "yyyyMMddTHHmmss", null,
            System.Globalization.DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        return DateTime.TryParse(value, out parsed) ? parsed : null;
    }

    // =========================================================================
    //  bak
    // =========================================================================

    public void CreateBakZip(List<string> allPaths, List<string> copyPaths, string zipName, ILogger logger)
    {
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeName = SanitizeFileName(zipName);
        var zipFn = string.IsNullOrEmpty(safeName) ? $"{ts}_backup.zip" : $"{ts}_{safeName}.zip";
        var zipFull = Path.Combine(_backupPath, zipFn);

        var tmpDir = Path.Combine(_backupPath, $"_tmp_bak_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var baked = new List<string>();
        var notFoundPaths = new List<string>();
        foreach (var path in allPaths)
        {
            var resolvedAny = false;
            foreach (var resolved in ResolveBgiPaths(path))
            {
                resolvedAny = true;
                var src = GetFullPath(resolved);
                if (!File.Exists(src))
                {
                    notFoundPaths.Add(resolved);
                    continue;
                }
                var dest = Path.Combine(tmpDir, resolved.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, true);
                baked.Add(resolved);
            }
            if (!resolvedAny)
                notFoundPaths.Add(path);
        }
        // 日志提示未命中路径
        foreach (var nf in notFoundPaths)
            logger.LogWarning($"备份路径未找到文件: {nf}");

        // 收集 copy zip 中的文件列表（用于 restore 时清理）
        var copyDeletedPaths = new List<string>();
        foreach (var cp in copyPaths)
        {
            var zipFile = Path.Combine(_copyPath, cp);
            if (!File.Exists(zipFile)) continue;

            try
            {
                using var fs = File.OpenRead(zipFile);
                using var zf = OpenZipWithGbkFallback(fs);
                foreach (ZipEntry entry in zf)
                {
                    if (entry.IsDirectory) continue;

                    var name = entry.Name.Replace('\\', '/');
                    // 清理路径会在还原时直接删除文件，必须先校验安全性，
                    // 否则恶意 copy zip 可借由清单 del 列表实现路径穿越。
                    if (IsSafeRelativePath(name))
                        copyDeletedPaths.Add(name);
                    else
                        logger.LogWarning($"跳过非法清理路径（可能的路径穿越）: {name}");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"读取 copy 压缩包失败，未记录其清理清单: {cp}  ({ex.Message})");
            }
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

        // 只在有实际备份/清理内容时才创建 zip
        bool hasContent = baked.Count > 0 || copyDeletedPaths.Count > 0;
        if (!hasContent)
        {
            TryCleanupTempDir(tmpDir, logger);
            logger.LogWarning($"备份路径未能解析到实际文件，未生成 zip");
            return;
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
        TryCleanupTempDir(tmpDir, logger);

        logger.LogSuccess($"备份完成（zip）: {zipFn}  共 {baked.Count} 个文件");
    }

    public static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray());

        sanitized = sanitized.Trim().TrimEnd('.');
        return sanitized.Length == 0 ? string.Empty : sanitized;
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
                                // 防御纵深：即使清单被篡改，还原时也再校验一次
                                if (!IsSafeRelativePath(p))
                                {
                                    logger.LogWarning($"跳过非法清理路径（可能的路径穿越）: {p}");
                                    continue;
                                }
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
                    TryCleanupTempDir(tmpDir, logger);
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
        TryCleanupTempDir(tmpDir, logger);
    }

    private static void TryCleanupTempDir(string dir, ILogger logger)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        catch (Exception ex)
        {
            logger.LogWarning($"清理临时目录失败: {dir}  ({ex.Message})");
        }
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

    public void ExecuteCopy(IEnumerable<string> copyQueries, ILogger logger)
    {
        foreach (var query in copyQueries)
            ExtractSingleCopy(query, logger);
    }

    private void ExtractSingleCopy(string zipQuery, ILogger logger)
    {
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
    /// 用 SharpZipLib 解压并校验路径安全性。文件名编码依据每个条目的语言编码
    /// 标志位(general purpose bit 11)判定：置位用 UTF-8，未置位回退到 GBK(936)
    /// ——中文 Windows 创建 zip 的常见编码。无需基于词表的启发式猜测。
    /// </summary>
    private int ExtractWithAutoEncoding(string zipFile, string destDir, ILogger logger)
    {
        int copied = 0;
        int skipped = 0;

        using var fs = File.OpenRead(zipFile);
        using var zf = OpenZipWithGbkFallback(fs);

        foreach (ZipEntry entry in zf)
        {
            if (entry.IsDirectory) continue;

            // 规范化路径：统一处理反斜杠/正斜杠
            var entryName = entry.Name.Replace('\\', '/').TrimStart('/');

            // 路径安全校验：阻止路径穿越攻击
            if (!IsSafePath(entryName, destDir))
            {
                logger.LogWarning($"跳过非法路径条目（可能的路径穿越）: {entryName}");
                skipped++;
                continue;
            }

            var targetPath = Path.Combine(destDir, entryName.Replace('/', Path.DirectorySeparatorChar));
            var targetDir = Path.GetDirectoryName(targetPath)!;

            Directory.CreateDirectory(targetDir);

            using (var entryStream = zf.GetInputStream(entry))
            using (var fileStream = File.Create(targetPath))
                entryStream.CopyTo(fileStream);

            copied++;
            logger.LogSuccess($"解压覆盖: {entryName} -> {targetPath}");
        }

        if (skipped > 0)
            logger.LogWarning($"本次解压跳过 {skipped} 个非法路径条目");

        return copied;
    }

    /// <summary>
    /// 以 GBK 作为非 Unicode 条目名的回退代码页打开 zip。SharpZipLib 在构造时即
    /// 依据每个条目的语言编码标志位(bit 11)解码文件名：置位用 UTF-8，未置位用
    /// 此处设定的 GBK(936)，覆盖中文 Windows 创建 zip 的常见情况。读取完毕即恢复
    /// 全局设置，避免影响其他调用。
    /// </summary>
    private static ICSharpCode.SharpZipLib.Zip.ZipFile OpenZipWithGbkFallback(Stream stream)
    {
        var codec = StringCodec.FromCodePage(GBK.CodePage); // 936
        return new ICSharpCode.SharpZipLib.Zip.ZipFile(stream, leaveOpen: false, stringCodec: codec);
    }

    /// <summary>
    /// 校验解压路径是否安全，防止路径穿越攻击（../、绝对路径、盘符路径）。
    /// 路径必须位于目标目录内，且不能包含上级目录引用。
    /// </summary>
    private static bool IsSafePath(string entryName, string destDir)
    {
        if (string.IsNullOrWhiteSpace(entryName))
            return false;

        // 拒绝包含 .. 的路径
        if (entryName.Contains(".."))
            return false;

        // 拒绝绝对路径（Windows 盘符或 Unix 绝对路径）
        if (entryName.Length >= 2 && entryName[1] == ':') // Windows 盘符 C:\
            return false;
        if (entryName.StartsWith('/') || entryName.StartsWith('\\')) // Unix / Windows 绝对路径
            return false;

        // 规范化后路径必须位于目标目录内
        var cleanName = entryName.Replace('\\', '/').TrimStart('/');
        var fullDestDir = Path.GetFullPath(destDir);
        if (!fullDestDir.EndsWith(Path.DirectorySeparatorChar))
            fullDestDir += Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullDestDir, cleanName));

        return fullPath.StartsWith(fullDestDir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 校验相对于 JsScript 根目录的相对路径是否安全（不含上级目录引用、
    /// 不是绝对/盘符路径）。用于备份清单中记录或还原的待删除路径，
    /// 防止路径穿越到 JsScript 目录之外。
    /// </summary>
    private bool IsSafeRelativePath(string relativePath)
        => IsSafePath(relativePath, GetFullPath(""));

    // =========================================================================
    //  文件原子操作与路径解析
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

    /// <summary>预览某个 copy 查询会匹配到的 zip 文件名（不解压），供试运行使用。</summary>
    public IReadOnlyList<string> FindCopyZipNames(string query)
        => FindCopyZip(query).Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToList();

    public void DeleteFile(string relativePath, ILogger logger)
    {
        var full = GetFullPath(relativePath);
        if (!File.Exists(full))
        {
            logger.LogWarning($"文件不存在，跳过删除: {full}");
            return;
        }
        File.Delete(full);
        logger.LogSuccess($"删除: {relativePath}");

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

    /// <summary>
    /// 解析 BGI JsScript 相对路径，支持通配符和目录展开。
    /// 输入为相对于 {BGIpath}\User\JsScript\ 的相对路径，返回解析后的相对路径列表。
    /// </summary>
    public IEnumerable<string> ResolveBgiPaths(string path)
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

        // 精确路径或文件路径：直接返回相对路径（确保使用正斜杠）
        yield return path.Replace('\\', '/');
    }

    private string MakeRel(string fullPath)
    {
        var prefix = GetFullPath("") + "\\";
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return fullPath;
        return fullPath.Substring(prefix.Length).Replace('\\', '/');
    }
}

public sealed record BackupInfo(
    string FileName,
    string FullPath,
    string ModuleName,
    DateTime? CreatedAt,
    int RestoreFileCount,
    int DeletePathCount,
    bool HasCopyCleanup)
{
    public string DisplayText
    {
        get
        {
            var created = CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知时间";
            var copy = HasCopyCleanup ? "含 copy 清理" : "无 copy 清理";
            return $"{ModuleName} | {created} | 备份 {RestoreFileCount} 个 | {copy} | {FileName}";
        }
    }

    public static BackupInfo FromFile(string fileName, string fullPath, DateTime createdAt)
    {
        return new BackupInfo(
            fileName,
            fullPath,
            Path.GetFileNameWithoutExtension(fileName),
            createdAt,
            0,
            0,
            false);
    }
}
