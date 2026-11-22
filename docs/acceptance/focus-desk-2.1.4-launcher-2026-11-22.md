# Focus Desk 2.1.4 软件栏稳定性验收

## 证据与修复

用户截图显示 WPS 文档已经可见，侧栏仍显示“正在打开”，随后显示“等待应用窗口超时”并回到首页。本机冻结入口为 `D:\WPS Office\ksolaunch.exe`；运行的窗口进程位于同一安装的版本化 `office6` 下，包含 `wps.exe`、`wpspdf.exe`、`wpp.exe`，部分路径使用 8.3 别名。旧代码只按启动 EXE 精确匹配，12 秒未匹配就报告超时，Dock 随后把首页置前。这解释了已打开却误判失败和被首页覆盖的表现，不等于证明 WPS 自身进程崩溃。

修复前先加入 `--verify-entry` 检查，仅查找既有窗口、绝不启动软件。旧代码失败，修复后同一 WPS 快捷方式通过。

本次改动：窗口身份比较展开长路径；WPS 套件启动器匹配同一根目录下版本化 `office6` 中的 `wps/wpspdf/wpp/et.exe`，并核对产品为 WPS Office、厂商一致。保留原冻结启动参数与 profile，不按标题或宽泛的同名进程匹配。删除“正在打开”提示；成功交给 Shell 启动后，12 秒的前台观察结束返回正常等待状态，不报超时，不把首页强制置前。共享启动观察最多保留两分钟以合并重复点击，退出 Dock 会取消观察；它不关闭、终止外部程序，也不在用户已经切换后强行激活迟到窗口。

参考：[Windows 统一启动与切换](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar-extensions#unified-launching-and-switching)、[GetLongPathNameW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getlongpathnamew)。本次实现固定入口的启动/复用/切换，不宣称复刻 Windows 任务栏所有菜单与缩略图功能。

## 验收

- `RuntimeFlow --slow-launch`：合成程序延迟 14 秒才创建窗口，验证无启动文字、12 秒后无超时错误或强制首页、等待期间重试仅启动一份、窗口迟到后仍存活且可以切换。已通过。
- `RuntimeFlow --formal --existing-app=<wps.exe> --entry-executable=<ksolaunch.exe>`：实际 WPS 既有窗口成功切换，15 秒后保持身份；正式 Dock/顶栏、工作区、WebView 草稿与 Cookie、计时器和硬性专注连续性检查通过，退出后任务栏、工作区和窗口位置恢复。原始结果一次受 Windows 前台切换许可限制而未通过；实际聚焦测试应用后重试通过，不绕过系统权限。
- 通过 computer-use 实际鼠标点击隔离 Dock 的 WPS 入口、返回入口、WPS 入口，已有 WPS 文档可见，侧栏无启动、超时文字。仅为工具选窗，在该次开发模式测试中打开了 Dock 的 `ShowInTaskbar`；产品默认行为未改。
- 合并后的源码再次通过音量/亮度 13 项检查、番茄钟 4 组回归、Dock 92 项纯检查。

## 合并构建及桌面升级

前版 2.1.3 的安装请求未获 UAC 完成，此次升级前桌面版为 2.1.2。当前源码包含 2.1.3 全部音量、亮度修复，本次统一打包为 2.1.4，产品提交 `47949610ba453976ae4b1255d0ecaef844b3a962`。self-contained Release、Setup 和便携 ZIP 构建通过。

| 产物 | SHA-256 |
| --- | --- |
| focus-desktop.exe | `f2624bf9a877a4471815945b19d5b999508ddc508cf37a840c49164ad729eb60` |
| FocusDesk-Setup-2.1.4.exe | `c3f601ac831572cf3207bf67587e09760976a156368bc948b5b1b5979337f4dc` |
| focus-desk-portable-2.1.4.zip | `5548ac211c67b0068f671f1b84f2ae3993dca5dbfb60eb0196458a85591999a8` |

升级前确认正式程序及真实 profile 的 WebView 已退出，在 LocalAppData 私有目录重新离线备份原程序及 profile；2252 个用户数据文件逐个核对，备份差异 0。

2026-11-22 14:44 覆盖安装退出码 0，未先卸载或清除数据。安装后版本 `2.1.4.0`，EXE 哈希与本次产物一致；原 AppId、安装目录及桌面快捷方式目标保持为 `C:\Program Files\FocusDesk\focus-desktop.exe`。安装后、启动正式 UI 前再次逐个核对 profile：2252 个文件，新增、删除及内容差异均为 0，配置和网页登录数据文件保留。

结构化证据：`D:\focus-desktop-handoff\dock-evidence\2026-11-22-2.1.4\installed-acceptance.json`；构建产物 `release/2.1.4-local-20261122`。本次为本机升级，未发布 GitHub Release。实际 WPS 验收在隔离 profile 的同一产品代码上完成；安装版以版本、二进制哈希、数据和入口核对确认，未把这些核对说成安装后再次完整鼠标验收。
