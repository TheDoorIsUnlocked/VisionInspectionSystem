using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VisionInspection.Core.Models;

namespace VisionInspection.UI.Views
{
    /// <summary>
    /// SOP模块视图
    /// </summary>
    public partial class SOPModuleView : UserControl
    {
        private List<SOPStepItem> _steps = new();
        private int _currentStepIndex = -1;

        public SOPModuleView()
        {
            InitializeComponent();
            InitializeSampleSteps();
        }

        /// <summary>
        /// 初始化示例步骤
        /// </summary>
        private void InitializeSampleSteps()
        {
            // 添加示例SOP步骤
            AddStep("1", "检测车辆", "使用YOLO模型检测画面中是否存在车辆", "🔍");
            AddStep("2", "车牌识别", "检测到车辆后，识别车牌号码", "📄");
            AddStep("3", "安全验证", "验证车牌是否在白名单中", "✓");
            AddStep("4", "触发PLC", "验证通过，触发PLC开门", "🔌");

            UpdateStepDisplay();
        }

        /// <summary>
        /// 添加步骤
        /// </summary>
        public void AddStep(string stepNumber, string name, string description, string icon)
        {
            var stepItem = new SOPStepItem
            {
                StepNumber = stepNumber,
                Name = name,
                Description = description,
                Icon = icon,
                Status = StepStatus.Pending
            };
            _steps.Add(stepItem);
        }

        /// <summary>
        /// 更新步骤显示
        /// </summary>
        private void UpdateStepDisplay()
        {
            StepsPanel.Children.Clear();

            for (int i = 0; i < _steps.Count; i++)
            {
                var step = _steps[i];
                var stepControl = CreateStepControl(step, i == _steps.Count - 1);
                StepsPanel.Children.Add(stepControl);
            }
        }

        /// <summary>
        /// 创建步骤控件
        /// </summary>
        private Border CreateStepControl(SOPStepItem step, bool isLast)
        {
            var border = new Border
            {
                Background = GetStepBackground(step.Status),
                BorderBrush = GetStepBorderBrush(step.Status),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, isLast ? 0 : 10),
                Padding = new Thickness(15),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 图标和步骤号
            var iconPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var iconText = new TextBlock
            {
                Text = step.Icon,
                FontSize = 24,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var numberText = new TextBlock
            {
                Text = $"步骤 {step.StepNumber}",
                FontSize = 10,
                Foreground = new SolidColorBrush(Colors.Gray),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0)
            };
            iconPanel.Children.Add(iconText);
            iconPanel.Children.Add(numberText);
            Grid.SetColumn(iconPanel, 0);
            grid.Children.Add(iconPanel);

            // 名称和描述
            var contentPanel = new StackPanel { Margin = new Thickness(15, 0, 0, 0) };
            var nameText = new TextBlock
            {
                Text = step.Name,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = GetStepForeground(step.Status)
            };
            var descText = new TextBlock
            {
                Text = step.Description,
                FontSize = 11,
                Foreground = new SolidColorBrush(Colors.Gray),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0)
            };
            contentPanel.Children.Add(nameText);
            contentPanel.Children.Add(descText);
            Grid.SetColumn(contentPanel, 1);
            grid.Children.Add(contentPanel);

            // 状态指示器
            var statusIndicator = CreateStatusIndicator(step.Status);
            Grid.SetColumn(statusIndicator, 2);
            grid.Children.Add(statusIndicator);

            border.Child = grid;
            return border;
        }

        /// <summary>
        /// 创建状态指示器
        /// </summary>
        private UIElement CreateStatusIndicator(StepStatus status)
        {
            return status switch
            {
                StepStatus.Completed => new TextBlock
                {
                    Text = "✓",
                    FontSize = 24,
                    Foreground = new SolidColorBrush(Colors.Green),
                    FontWeight = FontWeights.Bold
                },
                StepStatus.Running => new ProgressBar
                {
                    Width = 40,
                    Height = 40,
                    IsIndeterminate = true,
                    Style = (Style)FindResource("ProgressBarRing")
                },
                StepStatus.Failed => new TextBlock
                {
                    Text = "✗",
                    FontSize = 24,
                    Foreground = new SolidColorBrush(Colors.Red),
                    FontWeight = FontWeights.Bold
                },
                _ => new TextBlock
                {
                    Text = "○",
                    FontSize = 24,
                    Foreground = new SolidColorBrush(Colors.LightGray)
                }
            };
        }

        /// <summary>
        /// 获取步骤背景色
        /// </summary>
        private Brush GetStepBackground(StepStatus status)
        {
            return status switch
            {
                StepStatus.Completed => new SolidColorBrush(Color.FromRgb(246, 255, 237)),
                StepStatus.Running => new SolidColorBrush(Color.FromRgb(230, 247, 255)),
                StepStatus.Failed => new SolidColorBrush(Color.FromRgb(255, 241, 240)),
                _ => new SolidColorBrush(Colors.White)
            };
        }

        /// <summary>
        /// 获取步骤边框颜色
        /// </summary>
        private Brush GetStepBorderBrush(StepStatus status)
        {
            return status switch
            {
                StepStatus.Completed => new SolidColorBrush(Color.FromRgb(183, 235, 143)),
                StepStatus.Running => new SolidColorBrush(Color.FromRgb(145, 213, 255)),
                StepStatus.Failed => new SolidColorBrush(Color.FromRgb(255, 163, 158)),
                _ => new SolidColorBrush(Color.FromRgb(217, 217, 217))
            };
        }

        /// <summary>
        /// 获取步骤前景色
        /// </summary>
        private Brush GetStepForeground(StepStatus status)
        {
            return status switch
            {
                StepStatus.Completed => new SolidColorBrush(Color.FromRgb(82, 196, 26)),
                StepStatus.Running => new SolidColorBrush(Color.FromRgb(24, 144, 255)),
                StepStatus.Failed => new SolidColorBrush(Color.FromRgb(255, 77, 79)),
                _ => new SolidColorBrush(Color.FromRgb(102, 102, 102))
            };
        }

        /// <summary>
        /// 设置当前步骤
        /// </summary>
        public void SetCurrentStep(int stepIndex)
        {
            _currentStepIndex = stepIndex;
            for (int i = 0; i < _steps.Count; i++)
            {
                if (i < stepIndex)
                    _steps[i].Status = StepStatus.Completed;
                else if (i == stepIndex)
                    _steps[i].Status = StepStatus.Running;
                else
                    _steps[i].Status = StepStatus.Pending;
            }
            UpdateStepDisplay();
            StepTextBlock.Text = $"{stepIndex + 1}/{_steps.Count}";
        }

        /// <summary>
        /// 设置步骤结果
        /// </summary>
        public void SetStepResult(int stepIndex, bool success, string message = "")
        {
            if (stepIndex >= 0 && stepIndex < _steps.Count)
            {
                _steps[stepIndex].Status = success ? StepStatus.Completed : StepStatus.Failed;
                _steps[stepIndex].ResultMessage = message;
                UpdateStepDisplay();
            }
        }

        /// <summary>
        /// 添加日志
        /// </summary>
        public void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogTextBox.AppendText($"[{timestamp}] {message}\n");
            LogTextBox.ScrollToEnd();
        }

        /// <summary>
        /// 设置状态
        /// </summary>
        public void SetStatus(string status, Brush? foreground = null)
        {
            StatusTextBlock.Text = status;
            if (foreground != null)
            {
                StatusTextBlock.Foreground = foreground;
            }
        }

        /// <summary>
        /// 设置检测结果
        /// </summary>
        public void SetResult(string result, Brush? foreground = null)
        {
            ResultTextBlock.Text = result;
            if (foreground != null)
            {
                ResultTextBlock.Foreground = foreground;
            }
        }

        /// <summary>
        /// 配置按钮点击
        /// </summary>
        private void ConfigButton_Click(object sender, RoutedEventArgs e)
        {
            var configWindow = new SOPConfigWindow();
            configWindow.Owner = Window.GetWindow(this);
            
            // 传递当前步骤配置
            var configSteps = _steps.Select(s => new SOPConfigStep
            {
                Name = s.Name,
                Description = s.Description,
                Icon = s.Icon,
                DetectionType = "物体检测",
                ModelName = "默认YOLOv8模型"
            }).ToList();
            
            configWindow.SetSteps(configSteps);
            
            if (configWindow.ShowDialog() == true)
            {
                // 应用新配置
                var newSteps = configWindow.GetSteps();
                _steps.Clear();
                
                for (int i = 0; i < newSteps.Count; i++)
                {
                    AddStep((i + 1).ToString(), newSteps[i].Name, newSteps[i].Description, newSteps[i].Icon);
                }
                
                UpdateStepDisplay();
                AddLog("SOP配置已更新");
            }
        }

        /// <summary>
        /// 运行按钮点击
        /// </summary>
        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            // 模拟运行SOP检测
            RunSimulation();
        }

        /// <summary>
        /// 运行模拟
        /// </summary>
        private async void RunSimulation()
        {
            SetStatus("运行中", new SolidColorBrush(Color.FromRgb(24, 144, 255)));
            SetResult("检测中...", new SolidColorBrush(Colors.Gray));
            AddLog("开始SOP检测流程...");

            for (int i = 0; i < _steps.Count; i++)
            {
                SetCurrentStep(i);
                AddLog($"执行步骤 {i + 1}: {_steps[i].Name}...");

                // 模拟处理时间
                await System.Threading.Tasks.Task.Delay(1500);

                // 模拟结果（90%成功率）
                var success = new Random().Next(10) > 0;
                SetStepResult(i, success, success ? "成功" : "失败");

                if (success)
                {
                    AddLog($"步骤 {i + 1} 完成: {_steps[i].Name}");
                }
                else
                {
                    AddLog($"步骤 {i + 1} 失败: {_steps[i].Name}");
                    SetStatus("检测失败", new SolidColorBrush(Colors.Red));
                    SetResult("NG", new SolidColorBrush(Colors.Red));
                    return;
                }
            }

            SetStatus("检测完成", new SolidColorBrush(Color.FromRgb(82, 196, 26)));
            SetResult("OK", new SolidColorBrush(Color.FromRgb(82, 196, 26)));
            AddLog("SOP检测流程完成，结果: OK");
        }
    }

    /// <summary>
    /// SOP步骤项
    /// </summary>
    public class SOPStepItem
    {
        public string StepNumber { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Icon { get; set; } = "";
        public StepStatus Status { get; set; }
        public string ResultMessage { get; set; } = "";
    }

    /// <summary>
    /// 步骤状态
    /// </summary>
    public enum StepStatus
    {
        Pending,
        Running,
        Completed,
        Failed
    }
}
