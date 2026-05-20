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
                typeList.Add(step.op switch { "bak" => "备份", "del" => "删除", "copy" => "还原", _ => step.op });
            }
            _logger.LogInfo($"已选择模块: {module.name}  [{string.Join(" + ", typeList)}]");

            // 列出每个步骤的文件
            int idx = 1;
            foreach (var step in module.Steps)
            {
                string opLabel = step.op switch { "bak" => "备份", "del" => "删除", "copy" => "还原", _ => step.op };
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

            StatusText.Text = "执行中...";
            ExecuteBtn.IsEnabled = false;

            try
            {
                _fileManager = new FileManager(_configService.GetBGIPath(), _programPath);

                _logger.LogInfo($"开始执行模块: {module.name}");

                int totalFiles = 0;

                // 按 Steps 顺序逐步骤执行
                foreach (var step in module.Steps)
                {
                    totalFiles += step.paths.Count;
                    _fileManager.ExecuteStep(step, _logger);
                }


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
            }
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
