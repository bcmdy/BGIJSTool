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

    public OperationResult ExecuteModule(Module module)
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
            _logger.LogInfo($"Module backup: {module.name}, paths: {backupPaths.Count}");
            _fileManager.CreateBakZip(backupPaths.ToList(), copyPaths, module.name, _logger);
        }

        foreach (var step in moduleSteps)
        {
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

    public void RestoreBackup(string zipFileName)
    {
        _fileManager.ExecuteRestoreFromZip(zipFileName, _logger);
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
