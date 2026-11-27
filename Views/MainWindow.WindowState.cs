using System;
using Avalonia;
using Avalonia.Controls;
using BatchCompress.Avalonia.Core.Services;

namespace BatchCompress.Avalonia.Views;

// Gemini 4 Hydrogen, 2026-11-27：批次 46——从 MainWindow.axaml.cs 拆出纯窗口几何/状态恢复与保存逻辑，控制代码后置文件体积。
public partial class MainWindow
{
    private void RestoreWindowSize()
    {
        // 读取失败时 Load 返回 null，绝不阻止主窗口打开。
        var snapshot = _windowStateService.Load();
        if (snapshot is null)
        {
            return;
        }

        if (IsValidWindowDimension(snapshot.Width) && IsValidWindowDimension(snapshot.Height))
        {
            Width = snapshot.Width;
            Height = snapshot.Height;
            UpdateLastNormalSize(snapshot.Width, snapshot.Height);
        }

        if (snapshot.X.HasValue && snapshot.Y.HasValue)
        {
            UpdateLastNormalPosition(snapshot.X.Value, snapshot.Y.Value);
            Position = new PixelPoint(snapshot.X.Value, snapshot.Y.Value);
        }

        if (snapshot.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowSize()
    {
        var stateToSave = WindowState == WindowState.Minimized
            ? WindowState.Normal
            : WindowState;

        if (stateToSave == WindowState.Normal)
        {
            var width = ClientSize.Width > 0 ? ClientSize.Width : Width;
            var height = ClientSize.Height > 0 ? ClientSize.Height : Height;
            if (!IsLikelyMaximizedGeometry(new Size(width, height), Position))
            {
                UpdateLastNormalSize(width, height);
                UpdateLastNormalPosition(Position);
            }
        }

        if (!IsValidWindowDimension(_lastNormalWidth) || !IsValidWindowDimension(_lastNormalHeight))
        {
            return;
        }

        // Save 内部兜住所有 IO 异常，持久化失败不会阻塞退出。
        _windowStateService.Save(new WindowStateSnapshot(
            _lastNormalWidth,
            _lastNormalHeight,
            _hasLastNormalPosition ? _lastNormalX : null,
            _hasLastNormalPosition ? _lastNormalY : null,
            stateToSave == WindowState.Maximized));
    }

    private void UpdateLastNormalSize(double width, double height)
    {
        if (!IsValidWindowDimension(width) || !IsValidWindowDimension(height))
        {
            return;
        }

        _lastNormalWidth = width;
        _lastNormalHeight = height;
    }

    private void UpdateLastNormalPosition(PixelPoint point)
    {
        UpdateLastNormalPosition(point.X, point.Y);
    }

    private void UpdateLastNormalPosition(int x, int y)
    {
        _lastNormalX = x;
        _lastNormalY = y;
        _hasLastNormalPosition = true;
    }

    private static bool IsValidWindowDimension(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
    }

    private void ApplyLastNormalBounds()
    {
        if (!IsValidWindowDimension(_lastNormalWidth) || !IsValidWindowDimension(_lastNormalHeight))
        {
            return;
        }

        _isApplyingNormalBounds = true;
        try
        {
            ClientSize = new Size(_lastNormalWidth, _lastNormalHeight);
            Width = _lastNormalWidth;
            Height = _lastNormalHeight;

            if (_hasLastNormalPosition)
            {
                Position = new PixelPoint(_lastNormalX, _lastNormalY);
            }
        }
        finally
        {
            _isApplyingNormalBounds = false;
        }

        SaveWindowSize();
    }

    private bool IsLikelyMaximizedGeometry(Size clientSize, PixelPoint position)
    {
        try
        {
            var screens = Screens;
            var screen = screens.ScreenFromWindow(this) ?? screens.ScreenFromPoint(position) ?? screens.Primary;
            if (screen is null)
            {
                return false;
            }

            var workingArea = screen.WorkingArea;
            var widthClose = Math.Abs(clientSize.Width - workingArea.Width) <= 2;
            var heightClose = Math.Abs(clientSize.Height - workingArea.Height) <= 2;

            var xClose = Math.Abs(position.X - workingArea.X) <= 12 || position.X <= -6;
            var yClose = Math.Abs(position.Y - workingArea.Y) <= 12 || position.Y <= -6;

            return widthClose && heightClose && xClose && yClose;
        }
        catch
        {
            return false;
        }
    }
}
