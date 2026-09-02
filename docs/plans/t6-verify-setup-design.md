# T6 任务卡：verify-setup.ps1 — Setup Wizard 回归测试（设计稿，T4 完成后实施）

> 依赖：T4（SetupWizard.xaml/.cs）完成并暴露 AutomationId 约定后实施。
> 执行者：主会话（强模型）。预计 1.5 小时。

## 前提约定（T4 subagent 必须遵守，验收时核对）

Wizard 内所有可交互元素必须有显式 `AutomationProperties.AutomationId`：

| 元素 | AutomationId |
|---|---|
| 步骤1 路径显示 | `SetupPathText` |
| 步骤1 浏览按钮 | `SetupBrowseButton` |
| 步骤2 preset 勾选框 ×4 | `SetupPreset_bili` / `SetupPreset_chatgpt` / `SetupPreset_gemini` / `SetupPreset_deepseek` |
| 步骤2 自定义 URL 输入 | `SetupCustomUrlInput` |
| 步骤2 自定义名称输入 | `SetupCustomTitleInput` |
| 步骤2 添加按钮 | `SetupCustomAddButton` |
| 步骤2 添加失败提示 | `SetupCustomErrorText` |
| 步骤2 自定义列表项容器 | `SetupCustomList` |
| 步骤3 专注语 | `SetupFocusQuoteInput` |
| 步骤3 退出语 | `SetupExitPhraseInput` |
| 步骤3 番茄钟四参 | `SetupPomoWork` / `SetupPomoShort` / `SetupPomoLong` / `SetupPomoCycles` |
| 步骤4 选图按钮 | `SetupBgChooseButton` |
| 步骤4 清除按钮 | `SetupBgClearButton` |
| 底部 上一步 | `SetupBackButton` |
| 底部 预览 | `SetupPreviewButton` |
| 底部 主按钮 | `SetupNextButton`（完成步文案变为「完成并开始使用」）|
| 预览态返回按钮 | `SetupBackToWizardButton` |

## 测试用例（verify-setup.ps1）

> 运行前：备份 `release/focus-desktop/focus-desktop-data/config.json` 到临时目录，测试结束恢复（不动 Kevin 的真实配置）。测试全程操作一个临时 DataDir 副本。

1. **首启进向导**：删除 config.json → 启动（无参数）→ 断言主窗口 + `SetupNextButton` 存在 + 未进锁定（任务栏可见）。
2. **默认勾选**：断言 4 个 preset 勾选框 IsChecked=true；下一步到步骤2。
3. **自定义站点添加-成功**：`SetupCustomUrlInput` 填 `notion.so` → 点添加 → 断言 `SetupCustomList` 出现 1 项 + `SetupCustomErrorText` 不可见。
4. **自定义站点添加-失败**：填 `sub.bilibili.com` → 点添加 → 断言错误提示可见 + 列表不增。
5. **至少一站校验**：取消全部 preset 勾选 → 点下一步 → 断言仍停在步骤2 + 提示出现。
6. **步骤3 默认值**：前进到步骤3 → 断言专注语/退出语/番茄钟四参为默认值。
7. **非法番茄钟拦截**：工作时长填 `0` 或 `abc` → 点下一步 → 断言停在步骤3。
8. **预览无副作用**：到步骤4 → 点预览 → 断言向导层消失 + `SetupBackToWizardButton` 存在 + config.json **仍未生成** → 点返回 → 向导层恢复当前步骤。
9. **原子提交**：点「完成并开始使用」→ 断言 config.json 生成且含 `"configured": true` + `"schemaVersion": 2` + `sites` 含勾选 preset id + custom 条目（notion）→ 断言 `setup_done.flag` 生成 → 断言出现登录引导 banner。
10. **提交后不进向导**：杀掉进程 → 再启动 → 断言**无** `SetupNextButton`（不进向导）→ 断言 tab 条站点与提交配置一致（bili/chatgpt/gemini/deepseek/site）。
11. **中途强杀幂等**：删 config → 启动 → 填到步骤3 → 直接杀进程 → 断言 config.json 不存在或不含 `"configured": true` → 再启动仍进向导。
12. **Legacy 不进向导**：用 Kevin 真实 config.json（v1 无 schemaVersion/configured）放回原位 → 启动 → 断言不进向导 + tab 条 4 站与 v0.5.4 一致。
13. **清理**：杀进程 → 按 CommandLine 含 `focus-desktop-data` 清孤儿 msedgewebview2 → 恢复原 config.json。

## 红线
- 所有点击优先 UIA Invoke；失败退化物理坐标（2560×1600 @200%，逻辑×2=物理）。
- 每次断言前激活目标区域（Collapsed 元素不在 UIA 树）。
- 脚本结束必须自清理孤儿 webview。
- 新 .ps1 写 UTF-8 BOM；脚本内不写多字节字符直接量，用 `\u` 转义或按 ClassName 找。
