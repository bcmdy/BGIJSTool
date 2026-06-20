using BGIJSTool.Models;

namespace BGIJSTool.Services;

public sealed class ScriptOperationService
{
    private readonly FileManager _fileManager;
    private readonly ILogger _logger;

    public ScriptOperationService(FileManager fileManager, ILogger logger)
    {
        _fileManager = fileManager;
        _logger = logger;
    }

    public OperationResult ExecuteModule(Module module, IProgress<string>? progress = null)
    {
        var moduleSteps = module.Steps.ToList();
        var totalFiles = 0;
        var backupPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copyPaths = new List<string>();
        var hasBackupStep = false;

        foreach (var step in moduleSteps)
        {
            if (step.op is OpType.bak or OpType.del or OpType.restore)
            {
                foreach (var path in step.paths)
                {
                    var resolvedPaths = _fileManager.ResolveBgiPaths(path).ToList();
                    totalFiles += resolvedPaths.Count;

                    if (step.op is OpType.bak or OpType.del)
                    {
                        foreach (var resolvedPath in resolvedPaths)
                            backupPaths.Add(resolvedPath);
                    }
                }
            }

            if (step.op == OpType.bak)
                hasBackupStep = true;

            if (step.op == OpType.copy)
            {
                copyPaths.AddRange(step.paths);
                totalFiles += step.paths.Count;
            }
        }

        if (hasBackupStep && backupPaths.Count > 0)
        {
            progress?.Report("正在备份…");
            _logger.LogInfo($"Module backup: {module.name}, paths: {backupPaths.Count}");
            _fileManager.CreateBakZip(backupPaths.ToList(), copyPaths, module.name, _logger);
        }

        var stepIndex = 0;
        foreach (var step in moduleSteps)
        {
            stepIndex++;
            progress?.Report($"正在{OpLabel(step.op)}…（步骤 {stepIndex}/{moduleSteps.Count}）");
            switch (step.op)
            {
                case OpType.bak:
                    break;
                case OpType.del:
                    ExecuteDelete(step);
                    break;
                case OpType.restore:
                    ExecuteRestore(step);
                    break;
                case OpType.copy:
                    _fileManager.ExecuteCopy(step.paths, _logger);
                    break;
            }
        }

        return new OperationResult(totalFiles);
    }

    private static string OpLabel(OpType op) => op switch
    {
        OpType.bak => "备份",
        OpType.del => "删除",
        OpType.restore => "还原",
        OpType.copy => "复制",
        _ => op.ToString()
    };

    public void RestoreBackup(string zipFileName)
    {
        _fileManager.ExecuteRestoreFromZip(zipFileName, _logger);
    }

    /// <summary>
    /// 试运行：解析模块各步骤实际会命中的文件/压缩包，仅生成可读清单，不做任何改动。
    /// </summary>
    public IReadOnlyList<string> PreviewModule(Module module)
    {
        var lines = new List<string>();
        var stepIndex = 0;
        foreach (var step in module.Steps)
        {
            stepIndex++;
            var label = OpLabel(step.op);

            if (step.op == OpType.copy)
            {
                foreach (var query in step.paths)
                {
                    var matched = _fileManager.FindCopyZipNames(query);
                    lines.Add(matched.Count == 0
                        ? $"[{stepIndex} {label}] {query} -> （copy/ 下未找到匹配 zip）"
                        : $"[{stepIndex} {label}] {query} -> {string.Join(", ", matched)}");
                }
                continue;
            }

            foreach (var path in step.paths)
            {
                var resolved = _fileManager.ResolveBgiPaths(path).ToList();
                if (resolved.Count == 0)
                {
                    lines.Add($"[{stepIndex} {label}] {path} -> （无匹配文件）");
                    continue;
                }
                foreach (var rel in resolved)
                    lines.Add($"[{stepIndex} {label}] {rel}");
            }
        }
        return lines;
    }

    private void ExecuteDelete(Step step)
    {
        foreach (var path in step.paths)
        {
            foreach (var resolvedPath in _fileManager.ResolveBgiPaths(path))
                _fileManager.DeleteFile(resolvedPath, _logger);
        }
    }

    private void ExecuteRestore(Step step)
    {
        foreach (var path in step.paths)
        {
            foreach (var resolvedPath in _fileManager.ResolveBgiPaths(path))
                _fileManager.RestoreFile(resolvedPath, _logger);
        }
    }
}

public sealed record OperationResult(int TotalFiles);
