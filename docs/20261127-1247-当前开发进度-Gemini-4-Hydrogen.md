# 当前开发进度（0.8.32）

> 版本：`0.8.32`　分支：`main`

## 已完成

- `0.8.32`（批次 43 全仓路径比较按卷大小写敏感一致性收敛）：
  - 提升 `VolumeAwarePathComparer` 为共享内部比较器，全面替换 `CompressionItemAdapter`、`DecompressionItemAdapter`、`BatchOperationService`、`MainWindow.axaml.cs`、`FailedItemRetryList`、`HeadlessBatchRunner`、`DiagnosticsExportService`、`ApplicationInfoService` 中残留的静态平台/忽略大小写硬编码路径比较；新增 `TestCrossVolumePathIdentityConsistency` 并在真实 `Case-sensitive APFS` 卷上 **0-SKIP 跑通全部 122 项单元测试**。
- `0.8.31`（批次 42）：`CliCommandBuilder` + 开始页「复制 CLI」等价命令行导出（自定义密码强制 `--password-stdin` 脱敏）、补齐双页签 8 处悬停提示与五语言 376 键。
- `0.8.30`（批次 41）：顶部「重置偏好」按钮（带二次确认）及可滚动自适应高度多语言帮助对话框。
- `0.8.29`（批次 40）：安全 GUI 用户偏好持久化（`user-preferences.json` 原子写入与越界归一化，严禁保存明文密码、删除源与关机开关）。
- `0.8.28`（批次 39）：按卷大小写敏感检测（`pathconf` / 只读探测）、`scripts/verify-windows.ps1` 与三平台发布构建验证。

详见 [v0.8.32 完成报告](20261127-1247-v0.8.32全仓路径比较按卷大小写敏感一致性收敛完成报告-Gemini-4-Hydrogen.md)。

## 验收状态

Release 构建 0 警告 0 错误；真实 `Case-sensitive APFS` 卷上 122 项单元测试 **0 SKIP** 全过；5 步快速冒烟、43 项经验实测、158 项端到端夹具、4 项界面冒烟全部通过；macOS 应用 `0.8.32` 已安装并在后台运行。

## 质量边界

当前本机可完成的功能开发、数据安全边界、跨卷路径一致性、CLI/GUI 互通及自动化实测已全面收官；仅余依赖外部实体机器的 Windows / Linux 真机运行时验证。
