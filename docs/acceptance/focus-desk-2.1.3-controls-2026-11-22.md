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

安装前离线备份现有程序及用户数据；构建及覆盖安装结果在后续安装记录中填写。
