# 整体分析与下一步开发建议（0.8.33，只读）

> 用户原问题：整体分析项目，给出下一步开发详细建议  
> 时间：2026-11-27 12:55　版本：`0.8.33`（提交 `ce61532`）　模型：Gemini 4 Hydrogen  
> 说明：本轮为**只读分析与规划**，不修改任何产品源码，按项目规范不递增 `VERSION`。

---

## 1. 项目整体现状与健康度基线

- **技术栈与运行时**：.NET 11（[global.json](file:///Users/x/code/compress/global.json) 锁定 SDK `11.0.100`，目标框架 `net11.0`）+ Avalonia `12.1.6` + CommunityToolkit.Mvvm `8.4.2` + `Microsoft.Extensions.Logging 11.0.0` + `Tmds.DBus.Protocol 0.95.2`（见 [BatchCompress.Avalonia.csproj](file:///Users/x/code/compress/BatchCompress.Avalonia.csproj)）。
- **外部引擎依赖**：macOS / Linux x64 / Linux arm64 随包携带官方 7-Zip `26.05`（`tools/7zip/`）；Linux x64 完整随包携带官方 RAR Linux 11 文件分发包（`tools/rarLinux/`，提供 `--no-rar` 打包开关）；Windows 遵循不随包原则，由 [SevenZipProvisioner.cs](file:///Users/x/code/compress/Core/Services/SevenZipProvisioner.cs) 按需从官方发布页自动校验 SHA-256 下载安装，RAR 依赖用户本机 WinRAR。
- **工程规模（实测统计）**：
  - Git 提交 **320** 次，受跟踪文件 **514** 个，5 语言本地化（`zh-CN`、`zh-TW`、`en`、`ja`、`de`）共 **376** 个完全对齐词条；
  - 生产源码（`Core/`、`ViewModels/`、`Views/`、`Localization/` 及根 `.cs`）共 **18,106** 行；
  - 测试套件（`BatchCompress.Avalonia.Tests/*.cs`）共 **7,457** 行，**122** 项独立测试；
  - 文档目录 `docs/` **199** 份（`1.3 MB`）、`文档/` **105** 份（`536 KB`）、`测试夹具/` **60 MB**、`tools/` **13 MB**。
- **质量与交付基线（`v0.8.33` 实测）**：
  - Release 构建 **0 警告 0 错误**；
  - 挂载真实 `Case-sensitive APFS` 区分大小写卷实现 **122/122 单元测试 0-SKIP 全过**；
  - **158** 项全功能端到端夹具、`43` 项经验实测、`5` 步快速冒烟及 `4` 项桌面界面冒烟全部通过。

---

## 2. 架构优势与核心防线总结

过去连续十个迭代（`v0.8.23–v0.8.33`）已建立起非常扎实的核心数据安全与可用性防线：

1. **执行与后处理安全闭环**：
   - 压缩返回码 `1` 直接判失败并回滚残片（`v0.8.22`）；
   - 解压返回码 `1` 通过 [ExtractionWarningClassifier.cs](file:///Users/x/code/compress/Core/Services/ExtractionWarningClassifier.cs) 执行三层分级判定：写入/路径/未知警告默认判失败回滚，仅跨平台符号链接/元数据/尾部数据警告在默认模式下保留产物并通过方案 A 拦截删除/移动原压缩包；支持 `--strict-extract`（`v0.8.25–0.8.26`）；
   - 覆盖模式采用重命名 `.batchcompress-backup` 备份替换与启动恢复，失败/强杀均可恢复旧归档（[CompressionItemAdapter.cs](file:///Users/x/code/compress/Core/Services/Pipeline/CompressionItemAdapter.cs) / [OutputBackupRecovery.cs](file:///Users/x/code/compress/Core/Services/Pipeline/OutputBackupRecovery.cs)）；
   - [BatchPreflightService.cs](file:///Users/x/code/compress/Core/Services/BatchPreflightService.cs) 实现无副作用预检，并全面推行基于 `pathconf(_PC_CASE_SENSITIVE)`（macOS）与只读探测（Linux）的 `FileSystemPathIdentity` / `VolumeAwarePathComparer`，在 `v0.8.32–0.8.33` 完成全仓 9 处路径比较一致性收敛。
2. **界面与命令行高一致性**：
   - 双入口统一默认参数（[ArchiveDefaults.cs](file:///Users/x/code/compress/Core/Models/ArchiveDefaults.cs)）与日志落盘；
   - 界面支持安全偏好持久化与一键重置（[UserPreferencesService.cs](file:///Users/x/code/compress/Core/Services/UserPreferencesService.cs)，严禁落盘密码/删源/关机）、等价 CLI 一键复制脱敏导出（[CliCommandBuilder.cs](file:///Users/x/code/compress/Core/Services/CliCommandBuilder.cs)，`--password-stdin` 替代明文密码）、失败项一键重载重试（[FailedItemRetryList.cs](file:///Users/x/code/compress/ViewModels/FailedItemRetryList.cs)）以及实时日志搜索（`Ctrl+F`）。

---

## 3. 发现的真实改进点与技术债务清单

经源码与目录结构全面审计，确认以下值得优化的问题（按影响排序）：

### 3.1 【高体验价值】GUI 缺乏执行前「预检 / 试运行（Dry-Run）」入口与自定义密码掩码保护
- **现状证据**：
  - [BatchOperationService.cs](file:///Users/x/code/compress/Core/Services/BatchOperationService.cs#L419-L423) 已暴露专门供 GUI 与 CLI 共用的无副作用方法 `InspectCompressionBatch(...)`，但当前 [MainWindowViewModel.Execution.cs](file:///Users/x/code/compress/ViewModels/MainWindowViewModel.Execution.cs) **完全未向用户暴露独立“预检”命令**，用户只能直接点击“压缩”，等到实际执行时才在失败日志里发现输出碰撞或来源冲突；解压侧也只能在点击执行或手动切换时看到缺卷/密码本未匹配诊断。
  - [MainWindow.axaml](file:///Users/x/code/compress/Views/MainWindow.axaml#L102-L103) 与解压页第 212 行中，`CustomPassword` 输入框为全明文显示，未提供 `PasswordChar="●"` 掩码与“显示/隐藏密码”切换，录屏或共享屏幕时容易暴露口令。

### 3.2 【中·可维护性】核心交互与解析文件再次突破千行，代码边界可进一步解耦
- **现状证据（`wc -l` 实测）**：
  - [MainWindowViewModel.cs](file:///Users/x/code/compress/ViewModels/MainWindowViewModel.cs)：**1,151 行**（因接手偏好持久化和标签页状态同步再次超过千行）；
  - [CommandLineHandler.cs](file:///Users/x/code/compress/CommandLineHandler.cs)：**1,099 行**（选项注册、词法切分、语言预扫描与参数合法性校验全在一个文件）；
  - [Views/MainWindow.axaml.cs](file:///Users/x/code/compress/Views/MainWindow.axaml.cs)：**929 行**；
  - [BatchCompress.Avalonia.Tests/Program.PipelineTests.cs](file:///Users/x/code/compress/BatchCompress.Avalonia.Tests/Program.PipelineTests.cs)：**1,886 行**。
- **具体耦合点**：
  - [CommandLineHandler.cs](file:///Users/x/code/compress/CommandLineHandler.cs#L130-L139) 与第 861 行在静态方法 `ParseArguments` 内部直接修改全局单例 `LocalizationService.Instance.CurrentLanguage` / `LanguageExplicitlySet`（解析器不应产生全局静态副作用，应由 `CommandLineParseOutcome` 返回语言解析元数据，由 [Program.cs](file:///Users/x/code/compress/Program.cs#L92) 统一应用）；
  - [MainWindowViewModel.Execution.cs](file:///Users/x/code/compress/ViewModels/MainWindowViewModel.Execution.cs#L407-L410) 与 [HeadlessBatchRunner.cs](file:///Users/x/code/compress/HeadlessBatchRunner.cs#L88-L91) 导出诊断包时绕过注入机制直接 `new RarArchiveEngine(), new SevenZipArchiveEngine()`；
  - [App.axaml.cs](file:///Users/x/code/compress/App.axaml.cs#L60) 注册了静态 `LocalizationService.Instance.PropertyChanged` 却未在桌面 `Exit` 事件退订并主动释放 `_statusBarHelper`。

### 3.3 【中·验证盲区】物理 Windows 与 Linux 实体机运行时尚未实际执行
- 本机 macOS（Apple Silicon）上虽已完成 `win-x64` / `linux-x64` / `linux-arm64` 三平台 `dotnet publish` 产物结构核查与 `Case-sensitive APFS` 区分大小写实测，但仍缺少真机实跑覆盖以下平台专属代码路径：
  - Windows：[SevenZipProvisioner.cs](file:///Users/x/code/compress/Core/Services/SevenZipProvisioner.cs) 联网自动下载/SHA-256 校验/MSI 解包、`WindowsConsoleBootstrap` 父控制台附加、`ERROR_SHARING_VIOLATION` 文件锁定、`SystemPowerService` `shutdown /s /t 60`；
  - Linux：`tools/rarLinux` 运行、`notify-send` 托盘通知、`ProbeUnixDirectoryCaseSensitivity` 外接 FAT32/exFAT 与原生 ext4 实机探测。

### 3.4 【低·仓库与目录卫生】遗留大体积未跟踪构建目录与文档归档整理
- 仓库根目录下残留旧路径 `BatchCompress.Avalonia/`（包含历史 `net9.0`/`net10.0` `bin/` + `obj/`，占 **236 MB** 磁盘空间，虽已被 `.gitignore` 忽略不入库，但容易误导新协作者以为存在子项目）；
- `docs/` 下已累积 **199 份**报告文档（`1.3 MB`），与根目录历史需求草稿 `压缩.md`、`解压.md` 混置，可按批次阶段归档早于 `v0.8.0` 的历史文档以提升索引可读性；
- `BatchCompress.Avalonia.Tests.csproj` 仍采用 `<EnableDefaultCompileItems>false</EnableDefaultCompileItems>` 手工列举 50+ `<Compile Include>`，长远可评估抽取无 UI 的 `BatchCompress.Core.csproj`（零 Avalonia 依赖）被主程序与测试工程以 `ProjectReference` 共同引用，从根上消除漏链 `CS0246` 问题。

---

## 4. 下一步开发详细建议（分批次可执行路线）

> 注：下列批次均遵循「改动产品源码才递增 0.0.1，纯文档/清理不改版本」原则；用户可任选其中一批或按序推进。

### 批次 44（建议 P1）：GUI 预检试运行（Dry-Run）与密码掩码保护
1. **新增 GUI「预检」入口**：
   - 在 `MainWindowViewModel.Execution.cs` 新增 `[RelayCommand] private async Task PreflightPreviewAsync()`：复用 [BatchOperationService.InspectCompressionBatch](file:///Users/x/code/compress/Core/Services/BatchOperationService.cs#L419)（压缩页）及 `ArchiveVolumeResolver` / `LoadArchivesFromFolder` / `LoadFilesFromTextFileWithDiagnostics`（解压页），统计预计待处理项、跳过项、缺失项、推荐并发度 `ConcurrencyAdvisor.Recommend()` 及输出碰撞/嵌套警告，将清晰汇总写入命令日志并定位显示结果提示，全程零磁盘写入、退出后不影响当前配置；
   - 在 [MainWindow.axaml](file:///Users/x/code/compress/Views/MainWindow.axaml)「开始」页操作按钮区新增五语言「预检」按钮及提示（5 个语言包同步补齐词条）。
2. **自定义密码掩码与切换开关**：
   - 在压缩页与解压页 `CustomPassword` 输入框增加 `RevealPassword="False"` / `PasswordChar="●"` 与可点击的显示切换按钮（双向绑定同一个 `ShowCustomPassword` 布尔状态，关闭界面绝不持久化明文密码）。
3. **验收标准**：
   - 存在 `OutputCollision` 或缺卷时，点击“预检”不创建输出目录且准确列出冲突来源与目标路径；
   - 新增预检服务层纯逻辑单元测试并在 `MainWindow.axaml` 守卫按钮接线与五语言词条一致性。

### 批次 45（建议 P2）：大文件架构拆解与解析器纯净化
1. **拆分超千行文件（零行为变更）**：
   - 从 `MainWindowViewModel.cs` 拆出 `MainWindowViewModel.Preferences.cs`（承载 `OperationTabState`、偏好加载/应用/重置/保存与页签切换同步），将主文件从 1,151 行降至 ~750 行；
   - 从 `CommandLineHandler.cs` 拆出 `CommandLineHandler.Validation.cs`（承载参数范围/格式/冲突合法性校验），使解析与校验职责单一清晰，两文件均控制在 600 行内；
   - 视需要从 `Program.PipelineTests.cs` 拆出 `Program.VolumeCaseTests.cs`。
2. **消除 `CommandLineHandler.ParseArguments` 静态副作用**：
   - 在 `CommandLineParseOutcome` 增加 `bool LanguageRequested` 与 `string? DetectedLanguage`，移除解析器内部对 `LocalizationService.Instance.LanguageExplicitlySet` 的直接写入（仍保证错误消息使用解析到的临时语言格式化，或将解析错误格式化所需语言上下文内聚），由 `Program.cs` 唯一同步到全局单例；同时为 `App.axaml.cs` 增加 `desktop.Exit` 退订与帮助进程回收钩子。
3. **验收标准**：
   - 全套 **122+** 项单元测试（特别包含 `TestExplicitLanguageFlagSurvivesToGui`、`TestUnifiedArchiveDefaults`、`TestUiLifecycleWiring`）与 158 项夹具零回归。

### 批次 46（建议 P2 · 条件允许时）：测试工程引用规范化与仓库卫生整理
1. **仓库卫生清理（可在本地直接完成，不影响源码）**：
   - 清理本地遗留未跟踪大目录 `BatchCompress.Avalonia/`（释放 236 MB 磁盘空间，同时保留 `.csproj` 中的防误编规则或确认根目录无嵌套产物）；
   - 将 `docs/` 下 `v0.8.0` 之前的历史完成报告/进度快照迁移至 `docs/archive/` 并更新 `文档索引.md` 锚点（需脚本逐条核对无断链）。
2. **Core 分层评估**：
   - 评估将 `Core/` 独立为 `BatchCompress.Core.csproj`（保留现有包名与命名空间，不影响 CLI/GUI 与 `tools/` 复制逻辑），令测试工程通过标准 `<ProjectReference>` 引用业务核心，告别手动 `<Compile Include>` 漏链风险（注意：`BatchCompress.Avalonia.Tests` 测试若干 `internal` 类型时需在核心工程保留 `InternalsVisibleTo`）。

### 外部物理环境验证（待真机执行）
1. **Windows 实体机验证**：在 Windows 10/11 运行：
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts/verify-windows.ps1
   ```
   重点观察：`SevenZipProvisioner` 从 GitHub 下载安装 7-Zip、独占文件锁（`System.IO.FileShare.None`）触发返回码 `1` 并成功回滚、`--strict-extract` 与命令行父控制台输出。
2. **Linux 实体机验证**：在 Ubuntu / Debian（及可选 Linux ARM64）运行：
   ```bash
   bash scripts/smoke-test.sh
   dotnet run -c Release --project BatchCompress.Avalonia.Tests/BatchCompress.Avalonia.Tests.csproj
   ```
   核对 `tools/rarLinux` 执行、7zz 回退与真实 ext4/挂载卷 `ProbeUnixDirectoryCaseSensitivity` 结果。

---

## 5. 总结

`0.8.33` 的批量压缩解压工具在**核心可靠性、数据安全（返回码 1 分级拦截与回滚、覆盖备份恢复、按卷大小写敏感一致性）、跨入口契约对齐及本地化完备度**上已达到成熟可交付水平。下一阶段最值得投入的是 **批次 44（GUI 预检试运行 + 自定义密码掩码）** 与 **批次 45（ViewModel/CLI 解析器超千行拆分 + 纯净化）**，配合目标 Windows/Linux 实体机的一轮运行验收，即可完成从“高可靠工具”到“结构精炼、交互防护完备的跨平台成熟版本（`0.9.x` / `1.0`）”的跃升。
