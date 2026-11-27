# 当前开发进度（0.8.30）

> 版本：`0.8.30`　分支：`main`

## 已完成

- `0.8.30`（批次 41 重置偏好快捷操作与多语言帮助对话框升级）：
  - `UserPreferencesService.Reset()` + 主窗口顶部「重置偏好」按钮（带二次确认、不覆盖命令行 `--lang` 且不影响附件/窗口位置）。
  - 帮助窗口升级为自适应高度的可滚动弹窗，更新五语言 `HelpBody` 纳入 `Ctrl+F`、严格解压分级、失败项一键重试与安全偏好说明（367 → **372 键**）。
- `0.8.29`（批次 40）：安全 GUI 用户偏好持久化（`user-preferences.json` 原子写与越界归一化，严禁持久化 `CustomPassword`/`DeleteSourceAfter`/`ShutdownAfterComplete`）。
- `0.8.28`（批次 39）：按卷大小写敏感检测（macOS `pathconf(_PC_CASE_SENSITIVE)` + Linux 只读探测）、`Case-sensitive APFS` 磁盘映像 **0-SKIP** 实测、`scripts/verify-windows.ps1` 与三平台发布构建验证。
- `0.8.27`（批次 38）：失败项一键加载重试（含密码本保持）、`COMMAND_LINE.md` 补全、158 项端到端夹具。

详见 [v0.8.30 完成报告](20261127-1115-v0.8.30重置偏好快捷操作与帮助窗口升级完成报告-Gemini-4-Hydrogen.md)。

## 验收状态

Release 构建 0 警告 0 错误；120 项单元测试、5 步快速冒烟、43 项经验实测、158 项端到端夹具、4 项界面冒烟全部通过；macOS 应用 `0.8.30` 已安装并在后台运行。

## 质量边界

本机 macOS 可执行的全部功能开发、安全边界加固、偏好持久化及自动化实测均已收官。仅余依赖实体硬件环境的物理 Windows（已提供 `scripts/verify-windows.ps1` 一键脚本）与物理 Linux 真机运行时验证。
