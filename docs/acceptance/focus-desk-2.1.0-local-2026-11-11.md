# Focus Desk 2.1.0 本机实施与验收

## 范围与指纹

从 master `72b3386274f46ca279e3ca503fabecbceb3f7370` 接续已有未提交的白底 TXT/MD 修复，实现应用发现与选择、安全文本阅读、音量/亮度浮层。产品代码冻结在 `c36806c6fb58a5b05b3342490600fcbce66b70d1`。没有推送远端或发布 GitHub Release。

最终程序版本 `2.1.0.0`，ProductVersion `2.1.0+c36806c6fb58a5b05b3342490600fcbce66b70d1`。

| 产物 | SHA-256 |
| --- | --- |
| focus-desktop.exe | `e188fcbba9f615fac3f9054dccbc98687f3e546fae73e189932e6012a4baf638` |
| FocusDesk-Setup-2.1.0.exe | `73b19afe3745f6a28685cf93afea73d6f10607f5106c9a77567928b0aab980a8` |
| focus-desk-portable-2.1.0.zip | `783d27c3f3754cbdae9abab49c080cb190284344cfec24261b714ffd502e6c47` |

产物目录：`D:\focus-desktop-handoff\focus-desktop\release\2.1.0-local-20261111`。
合成测试日志及恢复脚本：`D:\focus-desktop-handoff\dock-evidence\2026-11-11-2.1.0`。
用户数据、网页登录数据及安装日志的备份只在当前账户的 LocalAppData 私有目录，不在仓库或产物中。

## 已通过的验证

| 验证 | 结果与边界 |
| --- | --- |
| 构建与依赖 | Debug / Release / self-contained 发布通过；Markdig 1.3.2（BSD-2-Clause）和 System.Management 10.0.0（MIT）的声明及许可证已纳入产物。 |
| 最终二进制纯逻辑自检 | `--dockselftest`：92 pass / 0 fail。包含旧 ID、参数/工作目录兼容、同名不同 EXE、同 EXE 去重和维护入口过滤。 |
| 本机应用发现 | 开始菜单、AppsFolder、桌面、HKCU/HKLM 32/64 位 App Paths 合并后 190 个候选，均通过入口校验且无重复窗口身份。Word、Excel、PowerPoint 均被发现。未承诺 Office 文档内置预览。 |
| 选择 | 合成同名应用不合并、同程序不同参数不重复；取消扫描通过；名称筛选、刷新后保留原已选 ID 和启动定义。真实鼠标搜索 Word/Excel、勾选 Excel、点击刷新后显示已选 2 个。 |
| TXT | 实际 WebView DOM 验证 UTF-8、带 BOM 的 UTF-16、GBK；中文、特殊字符、前导空行、制表符、3000 字长行完整；白底深色字。非法编码显示错误而不是静默替换字符。 |
| Markdown | 实际 WebView 中标题、列表、表格、代码块通过；原始 script 作为文字显示，无 script 元素；危险链接不能执行；远程图片不请求；学习目录内 PNG 可嵌入，越界图片拒绝。真实鼠标从学习文件打开 MD 并观察排版。 |
| 两种控制入口 | 真实鼠标确认无软件栏时顶栏双图标，有软件栏时左下固定双图标；同一面板内两根竖条；浮层位于 WebView 内容上方；Esc 与点击外部均收起。软件列表和底部区域独立布局。 |
| 真实硬件读写 | 实际拖动音量 27→34%，独立按钮解除/恢复静音；内屏亮度 50→62%，WMI 独立回读 62%。结束恢复音量 27%、静音 true、亮度 50%。亮度不支持分支用合成状态验证禁用滑条、显示“不可用”及原因。 |
| 正式模式回归 | `--formal --observe`：160 次页面/文件/首页、40 次夹具窗口往返。页面实例、草稿、Cookie、计时、Hard Focus、固定栏均保留。直接调用处理函数的循环不属于 160/40 次真实鼠标验收。 |
| 响应性 | 页面切换 P95 111.77 ms、最大 238.15 ms；外部窗口 P95 99.79 ms、最大 295.13 ms；UI 心跳 P95 67.15 ms、最大 119.64 ms。以上为本次合成测试观测。 |
| 无软件栏正式模式 | 跳过软件后冻结空名单、不创建 Dock、保留左上时钟；进入专注与正常退出后任务栏状态恢复。 |
| 最终包异常恢复 | 在带 portable.flag 的合成 profile 运行上表最终 EXE，分别终止主进程，以及同时终止其看门狗与主进程后重新启动。看门狗恢复和启动自愈均清除标记、恢复可见任务栏和原工作区。未终止真实用户程序。 |
| 安装策略 | 静态脚本检查及 10 项生产卸载身份判据通过。升级使用同一 AppId、原目录，未进入卸载清理链路。 |

## 过程中的失败及处理

- 第一轮文本测试暴露在后台线程访问 WebView 事件参数，已先在 UI 线程取得路径后再后台解析；渲染矩阵重跑通过。
- 正式夹具第一次运行受到 Windows 前台切换限制而失败；使用 `--observe` 提供真实前台点击后完整重跑通过。
- 一次把自检参数误写成 `--dock-selftest`，启动了调试主程序并加载既有数据。通过无数据删除参数的正常关闭助手退出；配置与 setup_done 标记保持原字节。网站加载造成 Cookie 数据库正常写入，没有把这种情况误记为字节不变。随后使用正确 `--dockselftest` 完成 92 项自检，并在安装前重新完整离线备份。
- 恢复脚本起初试图用正常关闭助手关闭 `--preview`；生产代码按设计保护预览/草稿窗口而拒绝。已通过真实鼠标点击退出该合成预览，再将脚本改为可正常关闭的开发模式，两个异常恢复场景重跑通过。

## 本机覆盖升级

安装身份 `{50D19490-82AC-42D3-95B2-F8EBEA860817}`；原安装目录 `C:\Program Files\FocusDesk`；数据目录 `%LOCALAPPDATA%\focus-desktop-dock`。升级前程序及真实 WebView 已退出，最初离线备份 1371 个 profile 文件；安装前再次离线备份并逐文件核验 1471 个文件。

用户批准 Windows UAC 后，指纹匹配的 Setup 使用 `/ALLUSERS /VERYSILENT /NORESTART /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS` 在原目录完成覆盖安装，退出代码 0；没有先卸载，没有运行带 `--remove-user-data` 的助手。

- 安装后、首次手动启动前，完整比较备份中的 1471 个数据文件，差异 0。随后运行网站产生的正常缓存/Cookie 更新不作为安装数据改写。
- 已安装 EXE 为 `2.1.0.0`，SHA-256 与上表最终测试程序一致；HKLM 原 AppId 下版本为 `2.1.0`，原安装目录不变。公共桌面 `Focus Desk.lnk` 仍指向 `C:\Program Files\FocusDesk\focus-desktop.exe`，安装目录没有 portable.flag。
- 从桌面入口实际启动后直接使用原配置，没有首配向导。保留六个软件入口、五个网站及原学习目录；实际网页界面确认 Bilibili、ChatGPT、Google AI Studio、DeepSeek、Gemini Notebook 的原账户会话仍在，没有重新认证或输入消息。
- 安装版验收后通过程序自身正常关闭助手退出（未带数据删除参数），退出代码 0。配置文件和 setup_done 标记仍与安装前备份逐字节相同，任务栏可见、工作区恢复原值、恢复标记为 clean。
- 安装版独立 Dock/顶栏没有出现在原生控制接口的可选窗口清单中。经两种受支持接口核对后，未继续绕过窗口选择限制；不将本次安装版的软件栏切换和浮层操作记为新增真实鼠标验收。开发阶段的两种浮层布局、真实拖动与最终包正式模式/异常恢复结果分别保留在上文，不能混称为完整安装版鼠标验收。

安装后结构化证据：`D:\focus-desktop-handoff\dock-evidence\2026-11-11-2.1.0\installed-acceptance.json`；正常退出后的桌面快照：同目录 `installed-after-normal-exit.json`。安装日志保留在私有备份目录，没有提交到仓库。
