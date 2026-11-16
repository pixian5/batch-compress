using System;
using BatchCompress.Avalonia.Localization;

namespace BatchCompress.Avalonia.ViewModels;

// WorkBuddy, 2026-11-16：0.8.13——界面状态类此前没有释放路径：
// 语言服务是静态单例，匿名订阅会一直持有 ViewModel；文件日志器也要等进程退出才释放。
// 这里实现幂等的 Dispose，由窗口关闭或数据上下文切换时调用。
public partial class MainWindowViewModel : IDisposable
{
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 退订静态语言服务，避免关闭后的实例继续收到属性变更。
        LocalizationService.Instance.PropertyChanged -= _languageChangedHandler;

        // 释放界面专用文件日志器（设计器模式下为 null）。
        _fileLogger?.Dispose();
    }
}
