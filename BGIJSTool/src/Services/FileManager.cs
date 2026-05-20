using System.IO;
using System.Text;
using System.Collections.Generic;
using BGIJSTool.Models;

namespace BGIJSTool.Services
{
    public class FileManager
    {
        private readonly string _bgiPath;
        private readonly string _programPath;
        private readonly string _backupPath;
        private readonly string _copyPath;

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
        /// 废弃：直接调用 BackupFile / DeleteFile / RestoreFile / CopyFromDirFile。
        /// </summary>
        public void ExecuteStep(Step step, ILogger logger)
        {
            foreach (var path in step.paths)
            {
                switch (step.op)
                {
                    case "bak":     BackupFile(path, logger); break;
                    case "del":     DeleteFile(path, logger); break;
                    case "restore": RestoreFile(path, logger); break;
                    case "copy":    CopyFromDirFile(path, logger); break;
                    default:        logger.LogWarning($"未知操作类型: {step.op}，跳过 {path}"); break;
                }
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
