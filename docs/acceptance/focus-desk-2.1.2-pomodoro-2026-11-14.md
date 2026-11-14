# Focus Desk 2.1.2 番茄钟修复

范围仅为 Hard Focus 结束后暂停按钮未恢复的问题。产品逻辑在 `PomodoroControl.Redraw` 的普通状态分支增加两行：恢复 `_btnPause.IsEnabled` 和 `_btnPause.Opacity`。其他计时、锁定、设置、文件及音量亮度行为未修改。

产品提交：`b5ba94e7bf07aef0c885e6f79a20bfc259dcbc2d`。

回归先于修复加入：旧代码在“专注结束后暂停应启用且正常显示”的断言失败；修复后以下四种组合均通过：正式/预览控件状态 × 短休息/长休息。每种检查 Hard Focus 工作段拒绝暂停、生产 `OnSecond` 路径完成后解除硬性专注并恢复按钮，以及休息和下一轮普通专注的按钮暂停/继续。

命令：`dotnet run --project tests/RuntimeFlow -- --pomodoro`。测试仅使用控件对象，调用按钮处理流程，临时调整内存中的阶段起始时间触发结束；不启动主窗口、不访问 profile、不操作桌面或硬件，不记为真实鼠标或完整专注模式验收。

Debug 回归构建和 self-contained Release / Setup / 便携 ZIP 构建通过。日志及结构化安装证据目录：`D:\focus-desktop-handoff\dock-evidence\2026-11-14-2.1.2`。

| 产物 | SHA-256 |
| --- | --- |
| focus-desktop.exe | `e972d46e3ab44297b2759b4e7b999e21649cfafe4e9273b1aa4e3e9d17fa7653` |
| FocusDesk-Setup-2.1.2.exe | `a025bfc3592fcaa28aa85fa46590c029022c09687fef0d8e48018e1f1578961e` |
| focus-desk-portable-2.1.2.zip | `a6783559aae4dad3d242e8fea6f35e536983fb3a8c147d8cc3b89974d56be72e` |

升级前正式程序及其 WebView 已退出，原程序与 profile 备份在当前账户的 LocalAppData 私有目录，1690 个数据文件逐个核对通过。安装器沿用原 AppId、原安装目录和数据路径，未先卸载，未运行带数据删除参数的助手；当前等待 Windows UAC 批准，尚未将覆盖安装记为完成。未发布 GitHub Release。
