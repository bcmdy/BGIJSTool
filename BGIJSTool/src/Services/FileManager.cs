using System.IO;
using System.Text;
using System.Collections.Generic;
using BGIJSTool.Models;

namespace BGIJSTool.Services
{
    public class FileManager
    {
        private readonly string _bgiPath;    // BetterGI/User/jsScript 根目录
        private readonly string _programPath;// 本程序所在目录（含 backup/ copy/）
        private readonly string _backupPath; // program/backup
        private readonly string _copyPath;   // program/copy

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
        {
            return Path.Combine(_bgiPath, "User", "JsScript", relativePath);
        }

        public string GetBackupPath(string relativePath)
        {
            return Path.Combine(_backupPath, relativePath);
        }

        public string GetCopySourcePath(string relativePath)
        {
            return Path.Combine(_copyPath, relativePath);
        }

        /// <summary>
        /// 按模块步骤依次执行（bak → del → restore → copy 或自定义顺序）。
        /// 支持 paths 中使用通配符（*.ext）和目录（dir/）自动展开。
        /// </summary>
        public void ExecuteStep(Step step, ILogger logger)
        {
            foreach (var path in step.paths)
            {
                IEnumerable<string> targets = ResolvePaths(path, step.op);
                foreach (var resolved in targets)
                {
                    switch (step.op)
                    {
                        case OpType.bak:     BackupFile(resolved, logger); break;
                        case OpType.del:     DeleteFile(resolved, logger); break;
                        case OpType.restore: RestoreFile(resolved, logger); break;
                        case OpType.copy:    CopyFromDirFile(resolved, logger); break;
                        default:             logger.LogWarning($"未知操作类型: {step.op.ToString()}，跳过 {resolved}"); break;
                    }
                }
            }
        }

        /// <summary>
        /// 将 paths 中的单个条目解析为实际文件列表：
        /// - 普通精确路径   → 返回自身
        /// - 通配符 *.ext   → 在 BGI JsScript 目录下匹配所有匹配文件
        /// - 目录（结尾为 / 或 \） → 递归遍历目录下所有文件
        /// </summary>
        private IEnumerable<string> ResolvePaths(string path, OpType op)
        {
            string trimmed = path.TrimEnd('/', '\\');

            // 通配符匹配（含路径前缀，如 "subdir/*.json" 或 "*.json"）
            if (path.Contains('*'))
            {
                var baseDir = Path.GetDirectoryName(GetFullPath(trimmed))
                               ?? GetFullPath(string.Empty);
                var pattern = Path.GetFileName(trimmed);
                if (!Directory.Exists(baseDir))
                    yield break;
                foreach (var f in Directory.GetFiles(baseDir, pattern, SearchOption.TopDirectoryOnly))
                    yield return MakeRelative(f);
                yield break;
            }

            // 目录展开：
            //   bak / del      → BGI JsScript 目录，GetFullPath
            //   copy           → 程序下 copy/   目录，_copyPath
            //   restore        → 程序下 backup/ 目录，_backupPath
            bool isDir = path.EndsWith("/") || path.EndsWith("\\")
                      || (Directory.Exists(GetFullPath(path)) && !File.Exists(GetFullPath(path)));
            if (isDir)
            {
                string dirFull;
                Func<string, string> makeRelative;

                if (op == OpType.copy)
                {
                    // copy：从 copy/ 展开，相对路径供 CopyFromDirFile → GetCopySourcePath 使用
                    dirFull = Path.Combine(_copyPath, trimmed);
                    makeRelative = f => f.Substring(_copyPath.Length).Replace('\\', '/').TrimStart('/');
                }
                else if (op == OpType.restore)
                {
                    // restore：从 backup/ 展开，相对路径供 RestoreFile → GetBackupPath 使用
                    dirFull = Path.Combine(_backupPath, trimmed);
                    makeRelative = f => f.Substring(_backupPath.Length).Replace('\\', '/').TrimStart('/');
                }
                else
                {
                    // bak / del：仍在 BGI JsScript 下展开
                    dirFull = GetFullPath(trimmed);
                    makeRelative = MakeRelative;
                }

                if (!Directory.Exists(dirFull))
                    yield break;
                foreach (var f in Directory.GetFiles(dirFull, "*", SearchOption.AllDirectories))
                    yield return makeRelative(f);
                yield break;
            }

            // 普通精确文件
            yield return path;

            string MakeRelative(string fullPath)
            {
                string relative = fullPath.Substring(GetFullPath("").Length)
                                  .Replace('\\', '/');
                return relative.TrimStart('/');
            }
        }

        public void BackupFile(string relativePath, ILogger logger)
        {
            var sourcePath = GetFullPath(relativePath);
            var backupPath = GetBackupPath(relativePath);

            if (!File.Exists(sourcePath))
            {
                logger.LogWarning($"文件不存在，跳过备份: {sourcePath}");
                return;
            }

            var dir = Path.GetDirectoryName(backupPath);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir!);
            }

            File.Copy(sourcePath, backupPath, true);
            logger.LogSuccess($"{sourcePath} -> {backupPath}");
        }

        public void DeleteFile(string relativePath, ILogger logger)
        {
            var fullPath = GetFullPath(relativePath);

            if (!File.Exists(fullPath))
            {
                logger.LogWarning($"文件不存在，跳过删除: {fullPath}");
                return;
            }

            File.Delete(fullPath);
            logger.LogSuccess(fullPath);
        }

        public void RestoreFile(string relativePath, ILogger logger)
        {
            var backupPath = GetBackupPath(relativePath);
            var targetPath = GetFullPath(relativePath);

            if (!File.Exists(backupPath))
            {
                logger.LogError($"备份文件不存在，无法还原: {backupPath}");
                return;
            }

            var dir = Path.GetDirectoryName(targetPath);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir!);
            }

            File.Copy(backupPath, targetPath, true);
            logger.LogSuccess($"{backupPath} -> {targetPath}");
        }

        /// <summary>
        /// 从程序目录下的 copy/ 文件夹复制文件到 BGI JsScript 目标路径。
        /// copy/ 目录结构与 BGI JsScript 保持一致，按相对路径定位源文件。
        /// </summary>
        public void CopyFromDirFile(string relativePath, ILogger logger)
        {
            var sourcePath = GetCopySourcePath(relativePath);
            var targetPath = GetFullPath(relativePath);

            if (!File.Exists(sourcePath))
            {
                logger.LogWarning($"copy 目录下文件不存在，跳过: {sourcePath}");
                return;
            }

            var dir = Path.GetDirectoryName(targetPath);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir!);
            }

            File.Copy(sourcePath, targetPath, true);
            logger.LogSuccess($"{sourcePath} -> {targetPath}");
        }
    }
}
