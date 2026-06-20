using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BGIJSTool.Models;
using BGIJSTool.Services;

namespace BGIJSTool
{
    public partial class MainWindow : Window
    {
        private static readonly Module PlaceholderModule = new()
        {
            name = "<-请选择需要执行的模块->",
            Steps = new()
        };

        private const string RestorePlaceholder = "（无备份）";

        private ConfigService _configService = null!;
        private Logger _logger = null!;
        private string _programPath = null!;
        private bool _isBusy;
        private bool _isBgiPathValid;

        public MainWindow()
        {
            InitializeComponent();
            RestoreCombo.SelectionChanged += (_, _) => UpdateActionState();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _programPath = AppDomain.CurrentDomain.BaseDirectory;
            _configService = new ConfigService(Path.Combine(_programPath, "config.json"));
            _logger = new Logger(LogBox, _programPath);

            LoadConfigIntoUi("配置加载成功");
        }

        private void LoadConfigIntoUi(string successMessage)
        {
            try
            {
                var config = _configService.LoadConfig();
                LogConfigWarnings();

                _isBgiPathValid = UpdateBGIPathDisplay(config.BGIpath);
                PopulateModuleCombo(_configService.GetModules());
                PopulateRestoreCombo();

                _logger.LogInfo(_isBgiPathValid
                    ? successMessage
                    : "BetterGI.exe 未找到，请浏览选择程序路径");
            }
            catch (Exception ex)
            {
                _logger.LogError($"配置加载失败: {ex.Message}");
                _isBgiPathValid = false;
                UpdateBGIPathDisplay(ex.Message);
                PopulateModuleCombo(null);
                PopulateRestoreCombo();
            }
            finally
            {
                UpdateActionState();
            }
        }

        private void LogConfigWarnings()
        {
            var validation = _configService.ValidateConfig();
            foreach (var warning in validation.Warnings)
                _logger.LogWarning($"配置警告: {warning}");
        }

        private bool UpdateBGIPathDisplay(string path)
        {
            string exePath = FindBetterGIExe(path);
            bool exeFound = !string.IsNullOrEmpty(exePath);

            BGIPathText.Text = exeFound
                ? $"BetterGI.exe: {exePath}"
                : $"BetterGI 路径: {path}  未找到 BetterGI.exe，请重新选择";
            BGIPathText.Foreground = _configService.IsValidPath() && exeFound
                ? Brushes.Black
                : Brushes.Red;
            return exeFound;
        }

        private static string FindBetterGIExe(string bgiPath)
        {
            if (string.IsNullOrEmpty(bgiPath) || !Directory.Exists(bgiPath))
                return string.Empty;

            var exe = Path.Combine(bgiPath, "BetterGI.exe");
            return File.Exists(exe) ? exe : string.Empty;
        }

        private void UpdateActionState()
        {
            ExecuteBtn.IsEnabled = !_isBusy && _isBgiPathValid && HasSelectedModule();
            PreviewBtn.IsEnabled = !_isBusy && _isBgiPathValid && HasSelectedModule();
            ExecuteRestoreBtn.IsEnabled = !_isBusy && _isBgiPathValid && HasSelectedBackup();
            DeleteBackupBtn.IsEnabled = !_isBusy && HasSelectedBackup();
            OpenLogDirBtn.IsEnabled = !_isBusy;
            ClearLogBtn.IsEnabled = !_isBusy;
            OpenConfigBtn.IsEnabled = !_isBusy;
            ValidateConfigBtn.IsEnabled = !_isBusy;
        }

        private bool HasSelectedModule()
        {
            return ModuleCombo.SelectedItem is Module module
                && !ReferenceEquals(module, PlaceholderModule)
                && !string.IsNullOrWhiteSpace(module.name)
                && module.Steps.Count > 0;
        }

        private bool HasSelectedBackup()
        {
            return RestoreCombo.SelectedItem is BackupInfo backup
                && !string.IsNullOrWhiteSpace(backup.FileName)
                && File.Exists(backup.FullPath);
        }

        private void SetBusy(bool isBusy, string status)
        {
            _isBusy = isBusy;
            StatusText.Text = status;
            UpdateActionState();
        }

        private void ModuleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateActionState();

            if (ModuleCombo.SelectedItem is not Module module || ReferenceEquals(module, PlaceholderModule))
                return;

            var typeList = new List<string>();
            foreach (var step in module.Steps)
            {
                typeList.Add(step.op switch
                {
                    OpType.bak => "备份",
                    OpType.del => "删除",
                    OpType.restore => "还原",
                    OpType.copy => "复制",
                    _ => step.op.ToString()
                });
            }

            _logger.LogInfo($"已选择模块: {module.name}  [{string.Join(" + ", typeList)}]");

            int index = 1;
            foreach (var step in module.Steps)
            {
                string opLabel = step.op switch
                {
                    OpType.bak => "备份",
                    OpType.del => "删除",
                    OpType.restore => "还原",
                    OpType.copy => "复制",
                    _ => step.op.ToString()
                };

                foreach (var path in step.paths)
                    _logger.LogInfo($"  [{index++} {opLabel}] {path}");
            }
        }

        private void PopulateModuleCombo(List<Module>? modules)
        {
            if (modules == null || modules.Count == 0)
            {
                ModuleCombo.ItemsSource = new[] { PlaceholderModule };
                ModuleCombo.SelectedIndex = 0;
                return;
            }

            var items = new List<Module> { PlaceholderModule };
            items.AddRange(modules);
            ModuleCombo.ItemsSource = items;
            ModuleCombo.SelectedIndex = 0;
        }

        private void PreviewBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ModuleCombo.SelectedItem is not Module module || !HasSelectedModule())
                return;

            _logger.LogInfo($"预览模块: {module.name}（试运行，不会修改任何文件）");
            var lines = CreateOperationService().PreviewModule(module);
            if (lines.Count == 0)
            {
                _logger.LogWarning("该模块没有可预览的内容");
                return;
            }
            foreach (var line in lines)
                _logger.LogInfo("  " + line);
            _logger.LogInfo($"预览完成，共 {lines.Count} 条（以上为将要处理的目标，未执行）");
        }

        private async void ExecuteBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ModuleCombo.SelectedItem is not Module module || !HasSelectedModule())
                return;

            // 执行含 del 的模块会永久删除真实脚本文件，先二次确认
            if (module.Steps.Any(s => s.op == OpType.del))
            {
                bool hasBackup = module.Steps.Any(s => s.op == OpType.bak);
                var backupHint = hasBackup
                    ? "该模块包含备份步骤，删除前会先生成备份。"
                    : "该模块不包含备份步骤，删除后将无法通过本工具还原！";
                var confirm = MessageBox.Show(
                    $"模块「{module.name}」包含删除操作，将永久删除目标脚本文件。\n{backupHint}\n\n确定要继续吗？",
                    "确认执行删除",
                    MessageBoxButton.YesNo,
                    hasBackup ? MessageBoxImage.Question : MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes)
                {
                    _logger.LogInfo($"已取消执行模块: {module.name}");
                    return;
                }
            }

            SetBusy(true, "执行中...");

            try
            {
                var operationService = CreateOperationService();
                _logger.LogInfo($"开始执行模块: {module.name}");

                // 在 UI 线程创建 Progress，使后台线程的进度回调自动切回 UI 线程更新状态栏。
                var progress = new Progress<string>(status => StatusText.Text = status);

                // 文件 IO 放到后台线程，避免阻塞 UI 线程导致界面假死；
                // Logger 通过 Dispatcher 回写 UI，后台线程写日志是安全的。
                var result = await Task.Run(() => operationService.ExecuteModule(module, progress));
                PopulateRestoreCombo();

                _logger.LogInfo($"批量操作统计: 计划处理 {result.TotalFiles} 个条目，详情见上方成功/警告/错误日志");
                _logger.LogInfo($"模块执行完成，共处理 {result.TotalFiles} 个文件");
                StatusText.Text = "执行完成";
            }
            catch (Exception ex)
            {
                _logger.LogError($"执行失败: {ex.Message}");
                StatusText.Text = "执行失败";
            }
            finally
            {
                SetBusy(false, StatusText.Text);
            }
        }

        private async void ExecuteRestoreBtn_Click(object sender, RoutedEventArgs e)
        {
            if (RestoreCombo.SelectedItem is not BackupInfo backup || !HasSelectedBackup())
            {
                _logger.LogWarning("请先选择一个备份压缩包");
                return;
            }

            SetBusy(true, "还原中...");

            try
            {
                _logger.LogInfo($"开始还原: {backup.DisplayText}");
                var operationService = CreateOperationService();
                await Task.Run(() => operationService.RestoreBackup(backup.FileName));
                StatusText.Text = "还原完成";
            }
            catch (Exception ex)
            {
                _logger.LogError($"还原失败: {ex.Message}");
                StatusText.Text = "还原失败";
            }
            finally
            {
                PopulateRestoreCombo();
                SetBusy(false, StatusText.Text);
            }
        }

        private void DeleteBackupBtn_Click(object sender, RoutedEventArgs e)
        {
            if (RestoreCombo.SelectedItem is not BackupInfo backup || !HasSelectedBackup())
            {
                _logger.LogWarning("请先选择一个备份压缩包");
                return;
            }

            var result = MessageBox.Show(
                $"确定要删除备份 {backup.FileName} 吗？\n此操作不可撤销。",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;

            SetBusy(true, "删除中...");

            try
            {
                File.Delete(backup.FullPath);
                _logger.LogSuccess($"已删除备份: {backup.FileName}");
                StatusText.Text = "删除完成";
            }
            catch (Exception ex)
            {
                _logger.LogError($"删除失败: {ex.Message}");
                StatusText.Text = "删除失败";
            }
            finally
            {
                PopulateRestoreCombo();
                SetBusy(false, StatusText.Text);
            }
        }

        private void PopulateRestoreCombo()
        {
            var selectedFileName = (RestoreCombo.SelectedItem as BackupInfo)?.FileName;
            var fileManager = new FileManager(_configService.GetBGIPath(), _programPath);
            var backups = fileManager.GetBackupInfos();

            RestoreCombo.ItemsSource = backups;
            RestoreCombo.IsEditable = false;
            RestoreCombo.IsEnabled = backups.Count > 0;

            if (backups.Count == 0)
            {
                RestoreCombo.Text = RestorePlaceholder;
            }
            else
            {
                RestoreCombo.SelectedItem = backups.FirstOrDefault(item => item.FileName == selectedFileName)
                    ?? backups[0];
            }

            UpdateActionState();
        }

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
                _isBgiPathValid = UpdateBGIPathDisplay(directory);
                UpdateActionState();

                if (!_isBgiPathValid)
                    _logger.LogError("所选目录下未找到 BetterGI.exe");
            }
        }

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
            LoadConfigIntoUi("配置已刷新");
        }

        private void OpenConfigBtn_Click(object sender, RoutedEventArgs e)
        {
            var path = _configService.ConfigPath;
            if (!File.Exists(path))
            {
                _logger.LogError($"配置文件不存在: {path}");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                _logger.LogInfo($"已打开配置文件: {path}（修改后请点“刷新配置”重新加载）");
            }
            catch (Exception ex)
            {
                _logger.LogError($"打开配置文件失败: {ex.Message}");
            }
        }

        private void ValidateConfigBtn_Click(object sender, RoutedEventArgs e)
        {
            var result = _configService.ValidateConfig();
            foreach (var error in result.Errors)
                _logger.LogError($"配置错误: {error}");
            foreach (var warning in result.Warnings)
                _logger.LogWarning($"配置警告: {warning}");

            if (!result.HasErrors && !result.HasWarnings)
                _logger.LogSuccess("配置校验通过，无错误或警告");
            else
                _logger.LogInfo($"配置校验完成：{result.Errors.Count} 个错误，{result.Warnings.Count} 个警告");
        }

        private void OpenLogDirBtn_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(_logger.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _logger.LogDirectory,
                UseShellExecute = true
            });
        }

        private void ClearLogBtn_Click(object sender, RoutedEventArgs e)
        {
            _logger.ClearCurrentLog();
            _logger.LogInfo("当前日志已清空");
        }

        private ScriptOperationService CreateOperationService()
        {
            var fileManager = new FileManager(_configService.GetBGIPath(), _programPath);
            return new ScriptOperationService(fileManager, _logger);
        }
    }
}
