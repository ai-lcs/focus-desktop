# Focus Desk 2.1.3 音量、亮度修复验收

本次范围：外层图标仅展开、收起对应面板；音量面板内的喇叭切换系统静音；Dock 内面板不超出侧栏。未扩展 Office 阅读、番茄钟或其他开发计划。

## 实现

- `SystemControls` 将音量、亮度分开显示，切换时替换当前面板。保留 Dock 内嵌和无 Dock 时顶栏弹层两种宿主。
- `VolumeHelper` 修正 Core Audio COM 的 HRESULT/BOOL 声明，并保留 `IMMDevice.OpenPropertyStore` 的原生方法槽位；每次操作重新取得当前默认输出，避免一直控制首次缓存的旧设备。
- 静音枚举当前所有活动播放输出，写入后逐一读回；界面按系统静音位显示，不再把音量为 0 当成已静音。系统外部改变状态后由现有轮询同步。

## 验证

`dotnet run --project tests/RuntimeFlow -- --system-controls` 通过 13 项：96 逻辑像素侧栏内音量单面板、外层点击不改变静音、收起、独立亮度、图标间切换、内层静音与恢复、默认设备音量写入与读回、宿主还原，以及顶栏对应面板的展开和关闭。测试使用独立 profile，结束恢复原音量及各输出设备的原静音状态。

`dotnet run --project tests/RuntimeFlow -- --system-controls-ui` 的同一产品控件另做真实鼠标验收：面板内喇叭进入静音时，Windows 自带音量合成器的系统扬声器主静音按钮显示“取消静音”，应用通道也显示静音；在 Windows 合成器取消主静音后，Focus Desk 图标恢复。随后实际点击外层喇叭收起，再点击太阳，只有亮度滑条出现在侧栏内，未覆盖右侧内容。

当前电脑实际活动播放输出为 1 个（Realtek 扬声器）。未做多个物理输出同时播放、耳机热插拔的硬件验收。测试证明当前机器的 Windows 系统主输出静音及同步，不把源代码遍历多设备等同于多设备实测。

`dotnet run --no-build --project tests/RuntimeFlow -- --pomodoro` 通过正式/预览 × 短/长休息的四组既有回归。

## 构建与安装

产品提交：`0fd918eb2dc587f46df21006c2cbd2b33bd493db`。self-contained Release、Setup 和便携 ZIP 构建通过，产物目录 `release/2.1.3-local-20261122`。

| 产物 | SHA-256 |
| --- | --- |
| focus-desktop.exe | `849075d15532318d5a1ee8d869fcbf8bb273f4ebf3976e7aa8ec99be6b5e3ed4` |
| FocusDesk-Setup-2.1.3.exe | `ec493ae3525b8a41941e88e42c7a8470c3c407fe557a39b92c1485862eca50db` |
| focus-desk-portable-2.1.3.zip | `7112619a8c914e7d61b7a0cea027ccfb6a4cae5e198b35139ded9ffc7253f819` |

安装前确认正式程序及其 WebView 未运行，离线备份当前 2.1.2 程序与用户 profile；2088 个数据文件逐个核对，备份差异 0。备份及安装日志仅存本机 LocalAppData 私有备份目录，不进入仓库。

已启动同 AppId、同目录的覆盖安装，当前等待用户批准 Windows UAC，尚未宣称桌面安装完成。安装后使用 `D:\focus-desktop-handoff\dock-evidence\2026-11-22-2.1.3\verify-installed.ps1` 核对版本、EXE 哈希、全部 profile 文件和桌面快捷方式，结构化结果写入同目录 `installed-acceptance.json`。不执行卸载，不清除用户数据，不发布 GitHub Release。
