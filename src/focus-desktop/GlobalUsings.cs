// WPF + WinForms 混用（WebView2 WinForms 控件经 WindowsFormsHost 承载）：
// 命名空间冲突全局消解——WPF 类型为默认
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using Button = System.Windows.Controls.Button;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Thickness = System.Windows.Thickness;
global using Orientation = System.Windows.Controls.Orientation;
global using HorizontalAlignment = System.Windows.HorizontalAlignment;
global using VerticalAlignment = System.Windows.VerticalAlignment;
