using System;
using System.Threading.Tasks;

namespace BatchCompress.Avalonia.ViewModels;

/// <summary>
/// 协调界面“删除源”勾选框的二次确认状态，防止重复弹窗并在取消或异常时安全回退。
/// </summary>
public sealed class DeleteSourceConfirmationGuard
{
    private bool _isConfirming;

    /// <summary>当前是否正处于等待用户确认的状态。</summary>
    public bool IsConfirming => _isConfirming;

    /// <summary>
    /// 尝试确认启用“删除源”。
    /// 未注入确认回调时直接返回 true，以兼容无头/设计器场景；确认进行中重入或发生异常时返回 false。
    /// </summary>
    public async Task<bool> ConfirmEnableAsync(Func<Task<bool>>? confirmAsync)
    {
        if (confirmAsync == null)
        {
            return true;
        }

        if (_isConfirming)
        {
            return false;
        }

        _isConfirming = true;
        try
        {
            return await confirmAsync();
        }
        catch
        {
            return false;
        }
        finally
        {
            _isConfirming = false;
        }
    }
}
