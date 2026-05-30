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
                : $"BetterGI 路径: {path}  ⚠ 未找到 BetterGI.exe，请重新选择";
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
            ExecuteRestoreBtn.IsEnabled = !_isBusy && _isBgiPathValid && HasSelectedBackup();
            DeleteBackupBtn.IsEnabled = !_isBusy && HasSelectedBackup();
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
            return RestoreCombo.SelectedItem is string zipFileName
                && !string.IsNullOrWhiteSpace(zipFileName)
                && File.Exists(Path.Combine(_programPath, "backup", zipFileName));
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

        private void ExecuteBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ModuleCombo.SelectedItem is not Module module || !HasSelectedModule())
                return;

            SetBusy(true, "执行中...");

            try
            {
                var operationService = CreateOperationService();
                _logger.LogInfo($"开始执行模块: {module.name}");

                var result = operationService.ExecuteModule(module);
                PopulateRestoreCombo();

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

        private void ExecuteRestoreBtn_Click(object sender, RoutedEventArgs e)
        {
            if (RestoreCombo.SelectedItem is not string zipFileName || !HasSelectedBackup())
            {
                _logger.LogWarning("请先选择一个备份压缩包");
                return;
            }

            SetBusy(true, "还原中...");

            try
            {
                _logger.LogInfo($"开始还原: {zipFileName}");
                CreateOperationService().RestoreBackup(zipFileName);
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
            if (RestoreCombo.SelectedItem is not string zipFileName || !HasSelectedBackup())
            {
                _logger.LogWarning("请先选择一个备份压缩包");
                return;
            }

            var result = MessageBox.Show(
                $"确定要删除备份 {zipFileName} 吗？\n此操作不可撤销。",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;

            SetBusy(true, "删除中...");

            try
            {
                var backupPath = Path.Combine(_programPath, "backup", zipFileName);
                File.Delete(backupPath);
                _logger.LogSuccess($"已删除备份: {zipFileName}");
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
            var backupDir = Path.Combine(_programPath, "backup");
            RestoreCombo.Items.Clear();
            RestoreCombo.IsEditable = false;

            if (!Directory.Exists(backupDir))
            {
                RestoreCombo.Text = RestorePlaceholder;
                RestoreCombo.IsEnabled = false;
                UpdateActionState();
                return;
            }

            var zips = Directory.GetFiles(backupDir, "*.zip", SearchOption.TopDirectoryOnly);
            Array.Sort(zips);

            RestoreCombo.IsEnabled = zips.Length > 0;

            if (zips.Length == 0)
            {
                RestoreCombo.Text = RestorePlaceholder;
            }
            else
            {
                foreach (var file in zips)
                    RestoreCombo.Items.Add(Path.GetFileName(file));
                RestoreCombo.SelectedIndex = 0;
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

        private ScriptOperationService CreateOperationService()
        {
            var fileManager = new FileManager(_configService.GetBGIPath(), _programPath);
            return new ScriptOperationService(fileManager, _logger);
        }
    }
}
