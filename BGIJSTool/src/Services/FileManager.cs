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

        public FileManager(string bgiPath, string programPath)
        {
            _bgiPath = bgiPath;
            _programPath = programPath;
            _backupPath = Path.Combine(programPath, "backup");
            Directory.CreateDirectory(_backupPath);
        }

        public string GetFullPath(string relativePath)
        {
            return Path.Combine(_bgiPath, "User", "JsScript", relativePath);
        }

        public string GetBackupPath(string relativePath)
        {
            return Path.Combine(_backupPath, relativePath);
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
    }
}
