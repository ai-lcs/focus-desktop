# Focus Desk 2.1.1 音量/亮度交互调整

依据用户标注截图，将双竖条调节区嵌入 96 像素软件栏底部，展开时不再使用向右的 Popup；底部扬声器直接静音/取消静音，太阳展开或收起调节区，删除额外文字静音按钮。无软件栏时保留顶栏入口，太阳打开原位置的调节面板。硬件读写、节流与轮询逻辑沿用 2.1.0。

产品提交：`5ae0cb8e2b2ffcd08bd8f8be4cf1aa0c27f5e525`。

验证结果：

- `RuntimeFlow --system-controls` 7 项通过：展开内容不越过 96 像素边界、不打开 Popup、两根滑条有实际布局、太阳收起、扬声器直接改变系统静音且不展开面板、仅两个图标按钮、同一控件可恢复到无侧栏顶栏并正常打开。
- 隔离窗口中真实鼠标点击太阳展开/收起、点击扬声器切换/恢复静音，截图确认右侧内容区不被遮挡。不是安装版独立 Dock 的新增鼠标验收；后者受既有原生窗口枚举限制。
- 本轮未拖动音量/亮度；原音量 0%、亮度 57% 保持不变，静音恢复原开启状态。
- 最终发布 EXE 的 `--dockselftest`：92 pass / 0 fail；安装策略静态检查与 10 项卸载身份判据通过。
- Debug 构建、self-contained Release 发布、Setup 与便携 ZIP 构建通过。没有发布 GitHub Release。

| 产物 | SHA-256 |
| --- | --- |
| focus-desktop.exe | `e6771adfd560bd593cc231f5596b912f7150e1ed0e44a7215b14ace7f1254d14` |
| FocusDesk-Setup-2.1.1.exe | `c519c8b4486e5e725edfe75bd0f68eb8e89fd8a0ef804681fb6210310effddca` |
| focus-desk-portable-2.1.1.zip | `a201c77729d78a0a5f7fdc900cd317f4cb1a0b8c2f12ed4de553dbb5918b5fb4` |

产物位于 `release\2.1.1-local-20261111`。合成日志位于 `D:\focus-desktop-handoff\dock-evidence\2026-11-11-2.1.1`。升级前程序及真实 WebView 已退出，程序和用户 profile 已离线备份到当前账户的 LocalAppData 私有目录，1690 个文件逐个核对通过；备份没有提交或打包。

覆盖安装已完成，安装器退出代码 0。沿用原 AppId、Program Files 安装路径与真实数据目录，没有先卸载。安装后程序版本 `2.1.1.0`，哈希与上表最终 EXE 一致；1690 个数据文件逐一比较，差异 0；公共桌面入口仍指向原安装路径。结构化证据为上述证据目录中的 `installed-acceptance.json`。安装日志保留在私有备份目录。
