using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BGIJSTool.Models;
using BGIJSTool.Services;

namespace BGIJSTool
{
    public partial class MainWindow : Window
    {
        private ConfigService _configService = null!;
        private FileManager _fileManager = null!;
        private Logger _logger = null!;
        private string _programPath = null!;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _programPath = AppDomain.CurrentDomain.BaseDirectory;
            _configService = new ConfigService(Path.Combine(_programPath, "config.json"));
            _logger = new Logger(LogBox, _programPath);

            try
            {
                var config = _configService.LoadConfig();
                bool pathValid = UpdateBGIPathDisplay(config.BGIpath);

                var modules = _configService.GetModules();
                PopulateModuleCombo(modules);
                PopulateRestoreCombo();

                EnableControls(pathValid);
                if (pathValid)
                    _logger.LogInfo("配置加载成功");
                else
                    _logger.LogWarning("BetterGI.exe 未找到，请浏览选择程序路径");
            }
            catch (Exception ex)
            {
                _logger.LogError($"配置加载失败: {ex.Message}");
                UpdateBGIPathDisplay(ex.Message);
                EnableControls(false);
            }
        }

        /// <returns>true 当 BetterGI.exe 存在</returns>
        private bool UpdateBGIPathDisplay(string path)
        {
            bool exeFound = UpdateBGIPathDisplayInternal(path);
            return exeFound;
        }

        /// <summary>实际更新 UI，返回 exe 是否存在</summary>
        private bool UpdateBGIPathDisplayInternal(string path)
        {
            string exePath = FindBetterGIExe(path);
            bool exeFound = !string.IsNullOrEmpty(exePath);

            if (exeFound)
            {
                BGIPathText.Text = $"BetterGI.exe: {exePath}";
            }
            else
            {
                BGIPathText.Text = $"BetterGI 路径: {path}  ⚠ 未找到 BetterGI.exe，请重新选择";
            }
            BGIPathText.Foreground = _configService.IsValidPath() && exeFound
                ? System.Windows.Media.Brushes.Black
                : System.Windows.Media.Brushes.Red;
            return exeFound;
        }

        /// <summary>在 BGIpath 目录下查找 BetterGI.exe，返回完整路径</summary>
        private static string FindBetterGIExe(string bgiPath)
        {
            if (string.IsNullOrEmpty(bgiPath) || !Directory.Exists(bgiPath))
                return string.Empty;
            var exe = Path.Combine(bgiPath, "BetterGI.exe");
            return File.Exists(exe) ? exe : string.Empty;
        }

        private void EnableControls(bool enabled)
        {
            ExecuteBtn.IsEnabled = enabled;
            ExecuteRestoreBtn.IsEnabled = enabled;
        }

        /// <summary>占位项，作为 ComboBox 提示用户的第一项</summary>
        private static readonly Module PlaceholderModule = new()
        {
            name = "<-请选择需执行的模块->",
            Steps = new()
        };

        private void ModuleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ModuleCombo.SelectedItem is not Module module || module == PlaceholderModule)
            {
                ExecuteBtn.IsEnabled = false;
                return;
            }

            ExecuteBtn.IsEnabled = true;

            // 列出本模块将要执行的操作（按 Steps 顺序）
            var typeList = new System.Collections.Generic.List<string>();
            foreach (var step in module.Steps)
            {
                typeList.Add(step.op switch { OpType.bak => "备份", OpType.del => "删除", OpType.restore => "还原", OpType.copy => "复制", _ => step.op.ToString() });
            }
            _logger.LogInfo($"已选择模块: {module.name}  [{string.Join(" + ", typeList)}]");

            // 列出每个步骤的文件
            int idx = 1;
            foreach (var step in module.Steps)
            {
                string opLabel = step.op switch { OpType.bak => "备份", OpType.del => "删除", OpType.restore => "还原", OpType.copy => "复制", _ => step.op.ToString() };
                foreach (var f in step.paths)
                    _logger.LogInfo($"  [{idx++} {opLabel}] {f}");
            }
        }

        /// <summary>用占位项 + 实际模块列表填充 ComboBox，默认选中占位</summary>
        private void PopulateModuleCombo(System.Collections.Generic.List<Module>? modules)
        {
            if (modules == null || modules.Count == 0)
            {
                ModuleCombo.ItemsSource = new[] { PlaceholderModule };
                ModuleCombo.SelectedIndex = 0;
                return;
            }
            var items = new System.Collections.Generic.List<Module> { PlaceholderModule };
            items.AddRange(modules);
            ModuleCombo.ItemsSource = items;
            ModuleCombo.SelectedIndex = 0;
        }

        private void ExecuteBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ModuleCombo.SelectedItem is not Module module) return;

            StatusText.Text = "执行中…";
            ExecuteBtn.IsEnabled = false;
            ExecuteRestoreBtn.IsEnabled = false;

            try
            {
                _fileManager = new FileManager(_configService.GetBGIPath(), _programPath);
                _logger.LogInfo($"开始执行模块: {module.name}");

                var moduleSteps = module.Steps.ToList();
                int totalFiles = 0;

                // 先第一遍扫描：收集 bak+del 路径，统计文件总数，决定是否需备份
                var bakPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var delPaths = new List<string>();
                bool hasBak = false;
                foreach (var s in moduleSteps)
                {
                    totalFiles += s.paths.Count;
                    if (s.op is OpType.bak or OpType.del)
                        foreach (var p in s.paths) bakPaths.Add(p);
                    if (s.op == OpType.bak) hasBak = true;
                    if (s.op == OpType.del) delPaths.AddRange(s.paths);
                }

                // 先备份（在删除之前，确保源文件还在）
                if (hasBak && bakPaths.Count > 0)
                {
                    _logger.LogInfo($"模块备份: {module.name}，共 {bakPaths.Count} 条路径");
                    _fileManager.CreateBakZip(bakPaths.ToList(), delPaths, module.name, _logger);
                }

                // 再执行具体操作
                foreach (var step in moduleSteps)
                {
                    switch (step.op)
                    {
                        case OpType.bak:     break;                      // 已提前处理
                        case OpType.del:     ExecuteDel(step, _logger);    break;
                        case OpType.restore: ExecuteRestore(step, _logger); break;
                        case OpType.copy:    ExecuteCopy(step, _logger);    break;
                    }
                }

                // 刷新还原下拉框
                PopulateRestoreCombo();

                _logger.LogInfo($"模块执行完成，共处理 {totalFiles} 个文件");
                StatusText.Text = "执行完成";
            }
            catch (Exception ex)
            {
                _logger.LogError($"执行失败: {ex.Message}");
                StatusText.Text = "执行失败";
            }
            finally
            {
                ExecuteBtn.IsEnabled = true;
                ExecuteRestoreBtn.IsEnabled = true;
            }
        }

        // ── 还原 ───────────────────────────────────────────────────────────

        private void ExecuteRestoreBtn_Click(object sender, RoutedEventArgs e)
        {
            if (RestoreCombo.SelectedItem is not string zipFileName
                || string.IsNullOrEmpty(zipFileName))
            {
                _logger.LogWarning("请先选择一个备份压缩包");
                return;
            }

            StatusText.Text = "还原中…";
            ExecuteBtn.IsEnabled = false;
            ExecuteRestoreBtn.IsEnabled = false;

            try
            {
                _fileManager = new FileManager(_configService.GetBGIPath(), _programPath);
                _logger.LogInfo($"开始还原: {zipFileName}");
                _fileManager.ExecuteRestoreFromZip(zipFileName, _logger);
                StatusText.Text = "还原完成";
            }
            catch (Exception ex)
            {
                _logger.LogError($"还原失败: {ex.Message}");
                StatusText.Text = "还原失败";
            }
            finally
            {
                ExecuteBtn.IsEnabled = true;
                ExecuteRestoreBtn.IsEnabled = true;
                PopulateRestoreCombo();
            }
        }

        // ── 还原 ComboBox ──────────────────────────────────────────────────

        private const string RestorePlaceholder = "（无备份）";

        private void PopulateRestoreCombo()
        {
            var backupDir = Path.Combine(_programPath, "backup");
            if (!Directory.Exists(backupDir))
            {
                RestoreCombo.Items.Clear();
                RestoreCombo.Text = RestorePlaceholder;
                RestoreCombo.IsEnabled = false;
                return;
            }

            var zips = Directory.GetFiles(backupDir, "*.zip", SearchOption.TopDirectoryOnly);
            Array.Sort(zips);

            RestoreCombo.IsEditable = false;
            RestoreCombo.IsEnabled = zips.Length > 0;
            RestoreCombo.Items.Clear();

            if (zips.Length == 0)
                RestoreCombo.Text = RestorePlaceholder;
            else
            {
                foreach (var f in zips)
                    RestoreCombo.Items.Add(Path.GetFileName(f));
                RestoreCombo.SelectedIndex = 0;
            }
        }

        // ── del / restore / copy 执行辅助 ──────────────────────────────────

        private void ExecuteDel(Step step, ILogger logger)
        {
            foreach (var p in step.paths)
                foreach (var resolved in ResolveBgi(p))
                    _fileManager.DeleteFile(resolved, logger);
        }

        private void ExecuteRestore(Step step, ILogger logger)
        {
            foreach (var p in step.paths)
                foreach (var resolved in ResolveBgi(p))
                    _fileManager.RestoreFile(resolved, logger);
        }

        private void ExecuteCopy(Step step, ILogger logger)
        {
            foreach (var p in step.paths)
                _fileManager.ExecuteCopy(new Step { paths = new() { p } }, logger);
        }

        /// <summary>解析 BGI JsScript 路径（支持通配符、目录）</summary>
        private IEnumerable<string> ResolveBgi(string path)
        {
            var trimmed = path.TrimEnd('/', '\\');

            if (path.Contains('*'))
            {
                var baseDir = Path.GetDirectoryName(_fileManager.GetFullPath(trimmed))
                                ?? _fileManager.GetFullPath("");
                var pattern = Path.GetFileName(trimmed);
                if (!Directory.Exists(baseDir)) yield break;
                foreach (var f in Directory.GetFiles(baseDir, pattern, SearchOption.TopDirectoryOnly))
                    yield return MakeRel(f);
                yield break;
            }

            if (path.EndsWith("/") || path.EndsWith("\\")
                || (Directory.Exists(_fileManager.GetFullPath(path))
                    && !File.Exists(_fileManager.GetFullPath(trimmed))))
            {
                var dir = _fileManager.GetFullPath(trimmed);
                if (!Directory.Exists(dir)) yield break;
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    yield return MakeRel(f);
                yield break;
            }

            yield return path;
        }

        private static string MakeRel(string fullPath)
        {
            var rel = fullPath.Replace('\\', '/');
            return rel.TrimStart('/');
        }

        /// <summary>
        /// 浏览并选择 BetterGI.exe，成功后自动写回 config.json
        /// </summary>
        private void BrowseBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 BetterGI.exe",
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                FileName = "BetterGI.exe"
            };

            if (dlg.ShowDialog(this) == true)
            {
                string selectedExe = dlg.FileName;
                string directory = Path.GetDirectoryName(selectedExe) ?? string.Empty;
                _logger.LogInfo($"已选择: {selectedExe}");
                SavePathToConfig(directory);
                bool ok = UpdateBGIPathDisplay(directory);
                EnableControls(ok);
                if (!ok)
                    _logger.LogError("所选目录下未找到 BetterGI.exe");
            }
        }

        /// <summary>将目录路径写回 config.json</summary>
        private void SavePathToConfig(string directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                _logger.LogError("目录为空，无法保存");
                return;
            }
            try
            {
                _configService.SaveBGIPath(directory);
                _logger.LogSuccess($"路径已保存: {directory}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"保存失败: {ex.Message}");
            }
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var config = _configService.LoadConfig();
                bool pathValid = UpdateBGIPathDisplay(config.BGIpath);

                var modules = _configService.GetModules();
                PopulateModuleCombo(modules);

                EnableControls(pathValid);
                _logger.LogInfo("配置已刷新");
            }
            catch (Exception ex)
            {
                _logger.LogError($"刷新配置失败: {ex.Message}");
                EnableControls(false);
            }
        }
    }
}
