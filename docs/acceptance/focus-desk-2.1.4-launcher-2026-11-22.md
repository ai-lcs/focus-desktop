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

前版 2.1.3 的安装请求未获 UAC 完成，系统仍为 2.1.2。当前源码包含 2.1.3 全部音量、亮度修复；本次统一打包为 2.1.4。安装前重新离线备份并核对全部用户数据，构建和安装结果后续补入。
