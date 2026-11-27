using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Avalonia.Metadata;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.Input;
using BatchCompress.Avalonia.Core.Services;
using BatchCompress.Avalonia.ViewModels;
using BatchCompress.Avalonia.Localization;
using Avalonia.Input;
using System.ComponentModel;
using System.Diagnostics;

namespace BatchCompress.Avalonia.Views;

public partial class MainWindow : Window
{
    private ScrollViewer? _commandLogScrollViewer;
    private TextBox? _sourceFileListTextBox;
    private double _lastNormalWidth;
    private double _lastNormalHeight;
    private int _lastNormalX;
    private int _lastNormalY;
    private bool _hasLastNormalPosition;
    private WindowState _lastWindowState = WindowState.Normal;
    private bool _isApplyingNormalBounds;
    // WorkBuddy, 2026-11-16：0.8.13——记录当前绑定的 ViewModel，切换数据上下文或关窗时释放。
    private MainWindowViewModel? _boundViewModel;
    // WorkBuddy, 2026-11-17：0.8.15——执行中关窗先确认、取消并等待清理，避免硬杀进程留下残片。
    private bool _closeApproved;
    private bool _closeDialogOpen;
    // WorkBuddy, 2026-11-16：批次 25（P2）——窗口状态读写收敛到 WindowStateService，
    // 设置文件路径与 JSON 字段保持不变，视图只负责采集当前尺寸与位置。
    private readonly WindowStateService _windowStateService = new();
    public MainWindow()
    {
        InitializeComponent();
        _lastNormalWidth = Width;
        _lastNormalHeight = Height;
        _lastNormalX = Position.X;
        _lastNormalY = Position.Y;
        _hasLastNormalPosition = true;

        RestoreWindowSize();
        _lastWindowState = WindowState;
        AddHandler(DragDrop.DropEvent, Drop);

        // 设置命令日志自动滚动。
        this.Resized += MainWindow_Resized;
        this.PositionChanged += MainWindow_PositionChanged;
        this.Closing += MainWindow_Closing;
        this.Loaded += MainWindow_Loaded;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != WindowStateProperty || change.NewValue is not WindowState newState)
        {
            return;
        }

        // GPT-5, 2026-08-05：部分平台后端在恢复时会短暂报告最大化几何信息。
        // 仅在 UI 完成状态切换后重新应用此前的普通窗口边界。
        if (_lastWindowState == WindowState.Maximized && newState == WindowState.Normal)
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (WindowState != WindowState.Normal || _isApplyingNormalBounds)
                {
                    return;
                }

                if (!IsLikelyMaximizedGeometry(ClientSize, Position))
                {
                    return;
                }

                ApplyLastNormalBounds();
            }, global::Avalonia.Threading.DispatcherPriority.Background);
        }

        _lastWindowState = newState;
        UpdateZoomButtonText();
        SaveWindowSize();
    }

    private void MainWindow_Resized(object? sender, WindowResizedEventArgs e)
    {
        if (WindowState == WindowState.Normal && !_isApplyingNormalBounds &&
            !IsLikelyMaximizedGeometry(e.ClientSize, Position))
        {
            UpdateLastNormalSize(e.ClientSize.Width, e.ClientSize.Height);
        }

        SaveWindowSize();
    }

    private void MainWindow_PositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (WindowState == WindowState.Normal && !_isApplyingNormalBounds &&
            !IsLikelyMaximizedGeometry(ClientSize, e.Point))
        {
            UpdateLastNormalPosition(e.Point);
        }

        SaveWindowSize();
    }

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved)
        {
            CompleteClose();
            return;
        }

        if (_boundViewModel is { IsOperating: true } viewModel)
        {
            // 先取消关闭，等用户确认并完成取消/清理后再真正关闭。
            e.Cancel = true;
            if (!_closeDialogOpen)
            {
                _ = ConfirmCloseDuringOperationAsync(viewModel);
            }

            return;
        }

        CompleteClose();
    }

    private void CompleteClose()
    {
        SaveWindowSize();

        // 退订静态语言服务并释放文件日志器；Dispose 幂等，重复调用安全。
        _boundViewModel?.Dispose();
    }

    private async Task ConfirmCloseDuringOperationAsync(MainWindowViewModel viewModel)
    {
        _closeDialogOpen = true;
        try
        {
            var confirmed = await ShowOkCancelMessageBoxAsync(
                L.CloseDuringOperationTitle,
                L.CloseDuringOperationBody,
                L.CloseDuringOperationExit,
                L.CloseDuringOperationContinue);
            if (!confirmed)
            {
                return;
            }

            viewModel.CancelOperationCommand.Execute(null);
            var completion = viewModel.WaitForOperationCompletionAsync();
            var finished = await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(10))) == completion;
            if (!finished)
            {
                var force = await ShowOkCancelMessageBoxAsync(
                    L.CloseDuringOperationTimeoutTitle,
                    L.CloseDuringOperationTimeoutBody,
                    L.CloseDuringOperationForceExit,
                    L.CloseDuringOperationContinue);
                if (!force)
                {
                    return;
                }
            }

            _closeApproved = true;
            Close();
        }
        catch (Exception ex)
        {
            // 对话框失败不能把程序卡在“关不掉”的状态；记录后允许用户再次尝试。
            Debug.WriteLine($"关窗确认失败: {ex.Message}");
        }
        finally
        {
            _closeDialogOpen = false;
        }
    }

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        UpdateZoomButtonText();
        // 查找命令日志滚动容器。
        _commandLogScrollViewer = this.FindControl<ScrollViewer>("CommandLogScrollViewer");
        _sourceFileListTextBox = this.FindControl<TextBox>("SourceFileListTextBox");
        
        // 订阅 CommandLog 属性变化。
        if (DataContext is MainWindowViewModel viewModel)
        {
            // WorkBuddy, 2026-11-16：0.8.12——窗口显示后在后台检查 7-Zip；
            // Windows 缺失时自动从官方渠道下载安装，不阻塞界面启动。
            _ = viewModel.EnsureArchiveToolingAsync();
        }
    }

    // GPT-5, 2026-08-06：保留旧 WinForms 的放大/缩小入口，使用 Avalonia 原生窗口状态实现。
    private void ZoomButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateZoomButtonText();
    }

    private void UpdateZoomButtonText()
    {
        // WorkBuddy, 2026-11-16：批次 25（P2）——按钮文案由 ViewModel 提供，视图只同步窗口状态。
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.IsMaximized = WindowState == WindowState.Maximized;
        }
    }
    
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CommandLog))
        {
            // 命令日志变化后自动滚动到底部。
            if (_commandLogScrollViewer != null)
            {
                // 通过调度器等待界面更新后再滚动。
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _commandLogScrollViewer.ScrollToEnd();
                }, global::Avalonia.Threading.DispatcherPriority.Background);
            }
        }

        if (e.PropertyName == nameof(MainWindowViewModel.CurrentSourcePath) &&
            sender is MainWindowViewModel viewModel)
        {
            if (string.IsNullOrWhiteSpace(viewModel.CurrentSourcePath))
            {
                ClearCurrentSourceHighlight();
            }
            else
            {
                HighlightCurrentSourceLine(viewModel.CurrentSourcePath, viewModel.CurrentFile);
            }
        }
    }

    private void ClearCurrentSourceHighlight()
    {
        if (_sourceFileListTextBox is null)
        {
            return;
        }

        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _sourceFileListTextBox.SelectionStart = 0;
            _sourceFileListTextBox.SelectionEnd = 0;
        }, global::Avalonia.Threading.DispatcherPriority.Background);
    }

    private void HighlightCurrentSourceLine(string sourcePath, string fileName)
    {
        if (_sourceFileListTextBox is null || string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var text = _sourceFileListTextBox.Text ?? string.Empty;
            var lineStart = 0;
            var lineNumber = 0;
            foreach (var line in text.Split('\n'))
            {
                var lineLength = line.TrimEnd('\r').Length;
                var candidate = line.Trim();
                if (PathsMatch(candidate, sourcePath))
                {
                    _sourceFileListTextBox.SelectionStart = lineStart;
                    _sourceFileListTextBox.SelectionEnd = lineStart + lineLength;
                    _sourceFileListTextBox.ScrollToLine(lineNumber);
                    return;
                }

                lineStart += line.Length + 1;
                lineNumber++;
            }
        }, global::Avalonia.Threading.DispatcherPriority.Background);
    }

    private static bool PathsMatch(string candidate, string sourcePath)
    {
        try
        {
            return FileSystemPathIdentity.GetComparer(sourcePath).Equals(
                Path.GetFullPath(candidate),
                Path.GetFullPath(sourcePath));
        }
        catch
        {
            return string.Equals(candidate, sourcePath, StringComparison.Ordinal);
        }
    }
    
    /// <summary>
    /// 获取当前本地化字符串集合。
    /// </summary>
    private LanguageStrings L => LocalizationService.Instance.Strings;

    private void Drop(object? sender, DragEventArgs e)
    {
        // GPT-5, 2026-08-05：仅接受一个可执行的拖放项目。目录成为扫描根目录，TXT 成为密码/列表来源，故有意忽略额外项目。
        if (e.DataTransfer.TryGetFiles() is IEnumerable<IStorageItem> files && DataContext is MainWindowViewModel viewModel)
        {
            foreach (var file in files)
            {
                var firstPath = file.Path.LocalPath;
                if (string.IsNullOrEmpty(firstPath)) continue;
                
                // GPT-5, 2026-08-05：拖入目录即为文件夹扫描模式的来源目录。
                if (Directory.Exists(firstPath))
                {
                    viewModel.SourceMode = 1;
                    viewModel.SaveFilePath = firstPath;
                    viewModel.AppendCommandLog(L.DroppedFolder + firstPath + "\n");
                    break;
                }
                // GPT-5, 2026-08-05：拖入 TXT 文件提供文件列表和可选密码。
                else if (File.Exists(firstPath) && firstPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.SourceMode = 0;
                    viewModel.SourcePath = firstPath;
                    viewModel.AppendCommandLog(L.DroppedTxtFile + firstPath + "\n");
                    break;
                }
            }
        }
    }
    
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        
        if (DataContext is MainWindowViewModel viewModel)
        {
            // WorkBuddy, 2026-11-16：0.8.13——切换数据上下文时先退订并释放旧实例；
            // 同一个实例重复设置时不重复订阅。
            if (!ReferenceEquals(_boundViewModel, viewModel))
            {
                if (_boundViewModel is { } previous)
                {
                    previous.PropertyChanged -= ViewModel_PropertyChanged;
                    previous.Dispose();
                }

                viewModel.PropertyChanged += ViewModel_PropertyChanged;
                _boundViewModel = viewModel;
            }

            // GPT-5, 2026-08-05：StorageProvider 和 Window.Hide 需要存活的 Avalonia 窗口。
            // 注入这些回调可使 ViewModel 不依赖平台控件。
            viewModel.BrowseSourceRequested = BrowseSourceAsync;
            viewModel.BrowseOutputRequested = BrowseOutputAsync;
            viewModel.BrowseTextFileRequested = BrowseTextFileAsync;
            viewModel.BrowseSaveFileRequested = BrowseSaveFileAsync;
            viewModel.BrowseAttachmentRequested = BrowseAttachmentAsync;
            viewModel.ShowHelpRequested = ShowHelpAsync;
            // WorkBuddy, 2026-11-16：批次 27——「关于」弹窗复用同一个 ViewModel 作为数据源。
            viewModel.ShowAboutRequested = ShowAboutAsync;
            viewModel.HideWindowRequested = HideWindowFromViewModel;
            viewModel.ConfirmShutdownCancellationRequested = ConfirmShutdownCancellationAsync;
            viewModel.ConfirmDeleteSourceRequested = ConfirmDeleteSourceAsync;
            viewModel.ConfirmResetPreferencesRequested = ConfirmResetPreferencesAsync;
            viewModel.ShowPreflightDialogRequested = ShowScrollableTextDialogAsync;
            viewModel.FocusLogSearchRequested = FocusCurrentLogSearchBox;
        }
    }

    private void HideWindowFromViewModel()
    {
        base.Hide();
    }

    private void FocusCurrentLogSearchBox()
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var controlName = viewModel.ActiveTab switch
        {
            3 => "SuccessLogSearchBox",
            4 => "FailLogSearchBox",
            5 => "CommandLogSearchBox",
            _ => null
        };
        if (controlName == null)
        {
            return;
        }

        var searchBox = this.FindControl<TextBox>(controlName);
        if (searchBox != null)
        {
            searchBox.Focus();
            searchBox.SelectAll();
        }
    }

    private async Task BrowseAttachmentAsync()
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = L.SelectAttachmentFolder,
            AllowMultiple = true
        });

        // GPT-5, 2026-08-05：将存储项目转换为路径，再与现有换行列表合并。
        // 不区分大小写的去重符合常见 Windows/macOS 文件系统行为，同时保留顺序。
        var paths = folders.Select(folder => folder.Path.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        var existing = viewModel.EnclosureList
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim())
            .Where(path => path.Length > 0)
            .ToList();
        foreach (var path in paths.Where(path => !existing.Contains(path, FileSystemPathIdentity.GetComparer(path))))
        {
            existing.Add(path);
        }

        viewModel.EnclosureList = string.Join(Environment.NewLine, existing);
    }

    private Task ShowHelpAsync()
    {
        // GPT-5, 2026-09-30：帮助对话框标题与正文改用本地化词条。
        // Gemini 4 Hydrogen, 2026-11-27：改用可滚动自适应高度对话框，避免五语言帮助正文被截断。
        return ShowScrollableTextDialogAsync(L.Help, L.HelpBody);
    }

    private async Task ShowScrollableTextDialogAsync(string title, string body)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 580,
            MaxHeight = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var closeButton = new Button
        {
            Content = L.Ok,
            Width = 100,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        closeButton.Click += (_, _) => dialog.Close(true);

        dialog.Content = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 16,
            Children =
            {
                new ScrollViewer
                {
                    MaxHeight = 400,
                    VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = new TextBlock
                    {
                        Text = body,
                        FontSize = 14,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 8, 0)
                    }
                },
                closeButton
            }
        };
        Grid.SetRow((Control)((Grid)dialog.Content).Children[0], 0);
        Grid.SetRow(closeButton, 1);

        await dialog.ShowDialog<bool>(this);
    }

    // WorkBuddy, 2026-11-16：批次 27——模态展示只读的「关于」窗口；
    // 与主窗口共用 DataContext，语言切换时弹窗内的绑定同步刷新。
    private async Task ShowAboutAsync()
    {
        var dialog = new AboutWindow
        {
            DataContext = DataContext,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        await dialog.ShowDialog(this);
    }

    private Task<bool> ConfirmShutdownCancellationAsync() =>
        ShowOkCancelMessageBoxAsync(L.ShutdownScheduledTitle, L.ShutdownConfirmBody, L.CancelShutdown, L.KeepShutdown);

    private Task<bool> ConfirmDeleteSourceAsync() =>
        ShowOkCancelMessageBoxAsync(
            L.ConfirmDeleteSourceTitle,
            L.ConfirmDeleteSourceBody,
            L.ConfirmDeleteSourceEnable,
            L.CancelDialog);

    private Task<bool> ConfirmResetPreferencesAsync() =>
        ShowOkCancelMessageBoxAsync(
            L.ConfirmResetPreferencesTitle,
            L.ConfirmResetPreferencesBody,
            L.Ok,
            L.CancelDialog);
    
    private async Task BrowseSourceAsync()
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;
        
        // 当前页决定 TXT 的含义：压缩页读取路径清单，解压页读取归档密码本。
        if (viewModel.SourceMode == 0 && viewModel.IsDecompressionTab)
        {
            // GPT-5, 2026-08-05：旧版 TXT 流程相对 SaveFilePath 解析文件名，
            // 因此允许选择列表文件前必须先确定该目录。
            if (string.IsNullOrWhiteSpace(viewModel.SaveFilePath))
            {
                bool shouldContinue = await ShowOkCancelMessageBoxAsync(L.Hint, L.SelectSaveDirectory);
                
                if (shouldContinue)
                {
                    // GPT-5, 2026-08-05：复用标准选择器，使每个平台遵循相同的路径规则。
                    await BrowseSaveFileAsync();
                    
                    if (string.IsNullOrWhiteSpace(viewModel.SaveFilePath))
                        return;
                }
                else
                {
                    return;
                }
            }
            
            // GPT-5, 2026-08-05：合并选择的文件名之前仅规范化末尾分隔符。
            string savePath = viewModel.SaveFilePath;
            if (!savePath.EndsWith(Path.DirectorySeparatorChar) && 
                !savePath.EndsWith(Path.AltDirectorySeparatorChar))
            {
                savePath += Path.DirectorySeparatorChar;
            }
            

            
            // GPT-5, 2026-08-05：优先显示文本文件，同时保留所有文件入口以兼容旧列表。
            var fileOptions = new FilePickerOpenOptions
            {
                Title = L.SelectPasswordTxt,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(L.TextFile) { Patterns = new[] { "*.txt" } },
                    new FilePickerFileType(L.AllFiles) { Patterns = new[] { "*.*" } }
                }
            };
            
            var files = await this.StorageProvider.OpenFilePickerAsync(fileOptions);
            if (files.Count > 0)
            {
                // GPT-5, 2026-08-05：仅保留选择的文件名并锚定到 SaveFilePath，以保持兼容性。
                string passwordFileName = Path.GetFileName(files[0].Path.LocalPath);
                
                string fullPath = Path.Combine(savePath, passwordFileName);
                
                viewModel.SourcePath = fullPath;
            }
        }
        else if (viewModel.SourceMode == 0 && viewModel.IsCompressionTab)
        {
            var fileOptions = new FilePickerOpenOptions
            {
                Title = L.SelectCompressionList,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(L.TextFile) { Patterns = new[] { "*.txt" } },
                    new FilePickerFileType(L.AllFiles) { Patterns = new[] { "*.*" } }
                }
            };
            var files = await StorageProvider.OpenFilePickerAsync(fileOptions);
            if (files.Count > 0)
            {
                viewModel.SourcePath = files[0].Path.LocalPath;
            }
        }
        else
        {
            var options = new FolderPickerOpenOptions
            {
                Title = L.SelectSourceFolder,
                AllowMultiple = false
            };
            
            var folders = await this.StorageProvider.OpenFolderPickerAsync(options);
            if (folders.Count > 0)
            {
                viewModel.SaveFilePath = folders[0].Path.LocalPath;
            }
        }
    }
    
    private async Task BrowseOutputAsync()
    {
        // GPT-5, 2026-08-05：输出选择始终是目录，且不会改变来源模式状态。
        var options = new FolderPickerOpenOptions
        {
            Title = L.SelectOutputFolder,
            AllowMultiple = false
        };
        
        var folders = await this.StorageProvider.OpenFolderPickerAsync(options);
        if (folders.Count > 0 && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.OutputPath = folders[0].Path.LocalPath;
        }
    }
    
    private async Task BrowseSaveFileAsync()
    {
        try
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                // GPT-5, 2026-08-05：记录选择器入口，用于诊断平台特定的存储提供程序失败。
                viewModel.AppendCommandLog("BrowseSaveFileAsync called\n");
            }
            
            var options = new FolderPickerOpenOptions
            {
                Title = L.SelectSaveFolder,
                AllowMultiple = false
            };
            
            // GPT-5, 2026-08-05：使用 Avalonia StorageProvider 而不是平台原生 API，以保持选择行为可移植。
            var folders = await this.StorageProvider.OpenFolderPickerAsync(options);
            
            if (DataContext is MainWindowViewModel viewModel2)
            {
                // GPT-5, 2026-08-05：保留返回数量，因为取消操作以空集合表示。
                viewModel2.AppendCommandLog($"Folder picker returned {folders.Count} items\n");
                
                if (folders.Count > 0)
                {
                    viewModel2.SaveFilePath = folders[0].Path.LocalPath;
                    viewModel2.AppendCommandLog($"Selected folder: {viewModel2.SaveFilePath}\n");
                }
            }
        }
        catch (Exception ex)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.AppendCommandLog($"Error in BrowseSaveFileAsync: {ex.Message}\n");
            }
        }
    }
    
    private async Task BrowseTextFileAsync()
    {
        // GPT-5, 2026-08-05：此选择器存储实际 TXT 路径，不同于上方旧版 SourceMode 路径构建逻辑。
        var options = new FilePickerOpenOptions
        {
            Title = L.SelectTextFile,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(L.TextFile) { Patterns = new[] { "*.txt" } },
                new FilePickerFileType(L.AllFiles) { Patterns = new[] { "*.*" } }
            }
        };
        
        var files = await this.StorageProvider.OpenFilePickerAsync(options);
        if (files.Count > 0 && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.TextFilePath = files[0].Path.LocalPath;
        }
    }
    
    private async Task<bool> ShowMessageBoxAsync(string title, string message)
    {
        // GPT-5, 2026-08-05：保持此对话框自包含，使调用方仅在用户确认后收到完成的 Task。
        Window? dialog = null;
        dialog = new Window
        {
            Title = title,
            Width = 440,
            MinHeight = 190,
            SizeToContent = SizeToContent.Height,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock { Text = message, FontSize = 16, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 20) },
                    new Button
                    {
                        Content = L.Ok,
                        Width = 100,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                }
            },
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        
        // GPT-5, 2026-08-05：以 true 关闭，使模态 Task 获得明确的成功确认结果。
        var button = (Button)((StackPanel)dialog.Content).Children[1];
        button.Click += (sender, e) => dialog.Close(true);
        
        return await dialog.ShowDialog<bool>(this);
    }
    
    private async Task<bool> ShowOkCancelMessageBoxAsync(
        string title,
        string message,
        string? confirmText = null,
        string? cancelText = null)
    {
        // GPT-5, 2026-08-05：使用明确布尔结果，区分用户取消与选择器失败。
        Window? dialog = null;
        dialog = new Window
        {
            Title = title,
            Width = 440,
            MinHeight = 190,
            SizeToContent = SizeToContent.Height,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock { Text = message, FontSize = 16, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 20) },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Spacing = 20,
                        Children =
                        {
                            new Button { Content = confirmText ?? L.Ok, Width = 100 },
                            new Button { Content = cancelText ?? L.CancelDialog, Width = 100 }
                        }
                    }
                }
            },
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        
        // 对话框创建后再绑定按钮点击事件。
        var buttonPanel = (StackPanel)((StackPanel)dialog.Content).Children[1];
        var okButton = (Button)buttonPanel.Children[0];
        var cancelButton = (Button)buttonPanel.Children[1];
        
        okButton.Click += (sender, e) => dialog.Close(true);
        cancelButton.Click += (sender, e) => dialog.Close(false);
        
        return await dialog.ShowDialog<bool>(this);
    }

}
