# focus-desktop Public v1.0 Engineering Plan

> 日期：2026-09-01 ｜ 作者：Principal Engineer (Hermes) ｜ 状态：待 Kevin 确认后分发 subagents
> 目标：从「Kevin 自用 portable」推进到「普通用户下载安装 → 首次 GUI 配置 → 长期使用」
> 基线：v0.5.4 (cf24024) + 工作区两处未提交修复（TabIdOf 崩溃修复、文件列表前景色修复）

---

## 0. 现状关键事实与 Invariants（动手前必须接受的前提）

### 0.1 已存在、可直接复用的资产（别重造）

| 资产 | 位置 | 状态 |
|---|---|---|
| Setup 状态机雏形 | `Services/FirstRunSetup.cs`（33 行）：`setup_done.flag` 标志，未完成→不锁定，点「开始专注」→ 写 flag → 立即锁定 | 逻辑对，UI 是一条简陋 banner（`MainWindow.xaml.cs:1045-1092`），本次扩成正式向导 |
| 配置原子写 | `Services/AtomicFile.cs`：tmp+move，无中间态 | 直接复用于 wizard 提交 |
| 配置容错加载 | `AppSettings.LoadOrDefault()`：损坏→回退默认值 | 扩展字段后保持此行为 |
| 域名匹配 | `UrlFilter.IsAllowed`：`host == d || host.EndsWith("."+d)` 后缀匹配，file:// 限学习目录子树 | 已处理子域/安全匹配，custom site 复用 |
| 白名单/登录域概念 | `AppSettings.Whitelist` + `LoginDomains`（含 Google/Auth0/B站 passport 实测登录域） | preset 元数据可从这两个默认列表反推 |
| WebView2 数据驱动基础 | `WebTabService.RegisterTab(id, title, url)` 已是通用 API；预热 `WarmupAllAsync` 遍历 `_tabs` 自动覆盖全部注册 tab | **只需要「注册哪些」变成配置驱动，服务层零改动** |

### 0.2 硬编码点（本次要拔掉的）——全部集中在 MainWindow.xaml.cs

| 位置 | 内容 |
|---|---|
| `MainWindow.xaml.cs:187-190` | smoke 模式注册 4 站 |
| `MainWindow.xaml.cs:237-240` | 正常模式注册 4 站 |
| `MainWindow.xaml.cs:458-464` | `QuickSites[]` 静态数组（「+」菜单 + 首页快捷入口共用） |
| `MainWindow.xaml:96-130` 区域 | 首页 4 个快捷入口按钮（Bili/ChatGPT/AI Studio/DeepSeek 各一个 x:Name + Click 处理器 `:666-669`） |
| `AppSettings.cs:22-40` | Whitelist/LoginDomains 默认值内嵌 5 域+5 登录域 |

### 0.3 不可触碰的红线（实机踩坑验证过的稳定层）

1. **锁定/恢复四件套**：`FocusModeService` / `TaskbarService` / `KeyboardGuardService` / `WatchdogService` + `RecoveryService` 脏标志协议。**零改动**。`--watchdog`/`--restore`/`--smoke`/`--urltest`/`--voltest`/`--preview` 参数语义不变。
2. **WebView2 切换架构**：全员常显 + `BringToFront()`；预热=隐藏创建 + 宿主 Collapsed；BackColor 压 #23262C；**`WebTabService.cs` 一行都不改**（数据驱动在它上游解决）。
3. **测试体系**：`tests/verify-step12.ps1`（13 项锁定回归）/ `verify-tabs.ps1` / `--urltest` 20/20 / `--smoke` 全部保留且必须保持绿。测试脚本对 exe 位置、数据目录、AutomationId（`tab_*`/`AddTabButton`）的依赖见 §0.5。
4. 无账号、无云、无服务器、无数据库、单工程、local-first、Windows-only。
5. UI 深色系：任何代码创建的 TextBlock/Button 显式 `Foreground`；新增控件 BackColor 压主题色。

### 0.4 数据布局现状（要改的）

```
现状（portable）：exe 旁 focus-desktop-data/
  config.json  session_state.json  setup_done.flag  smoke.log  logs/  WebViewProfile/
```
`Paths.cs`（19 行）把 DataDir 硬钉在 `AppContext.BaseDirectory`——装进 Program Files 后这里不可写，**必须改**。

### 0.5 测试/运维对数据目录的隐性依赖（改布局时必须同步）

- `verify-step12.ps1` 等脚本按 `release\focus-desktop\focus-desktop-data\` 定位 config/session_state/logs；
- 孤儿 WebView 清理按 CommandLine 含 `focus-desktop-data` 过滤；
- `make-shortcuts.ps1` 指向 `D:\focus-desktop\release\focus-desktop\`。
→ 策略：**portable 布局一字不动**（目录名保持 `focus-desktop-data`），installed 走全新 LocalAppData 路径，两条路径在 `Paths` 单点分流。

---

## 1. 关键架构决策（ADR）

### ADR-1 配置状态机：Draft / Frozen / Legacy 三态

**决策**：`config.json` 增加 `"schemaVersion": 2` 与 `"configured": true|false`。状态判定：

```
文件不存在或 configured 缺失/false        → Draft（进向导）
configured == true                       → Frozen（正常运行，UI 无配置入口）
configured == true 且 schemaVersion 缺失  → Legacy Frozen（v0.5.x Kevin 自用配置：直接视为已完成，
                                            站点走 BuiltInSites 默认 4 站 + 既有 whitelist，绝不进向导）
```

- **冻结的实现 = 没有 UI**，不是权限锁：Frozen 后整个 app 不含任何修改这些设置的入口；重配的正式路径 = 卸载重装（卸载程序清数据）。
- 不加密码/哈希校验「防手改 config.json」——产品哲学是行为摩擦不是防篡改，手改 JSON 的高级用户自担后果（与现状一致）。
- 现有 `setup_done.flag` **保留不动**：它仍表示「首次锁定已激活过」，是锁定层语义；wizard 用 `configured` 字段。两者各司其职，RecoveryService 不感知 wizard。

### ADR-2 Wizard 进程内、Preview 式、原子提交

**决策**：Wizard 是 MainWindow 启动时全屏盖在 HomeView 上的 WPF 层（新文件 `SetupWizard.xaml/.cs`），不走独立进程，**不进锁定**（同现有 Setup 模式语义：首次未完成前 `App.OnStartup` 不 `_focus.Enter()`）。

Draft 生命周期：
1. wizard 打开时从当前 config（或默认值）建内存 draft 副本；
2. 「Preview」按钮 = 关掉 wizard 层直接看主界面（草稿不落盘、不产生任何 tab——见 ADR-3，首页/文件页立刻可看，网页 tab 用配置里的站）；
3. 「确认完成」= **一次原子写**：序列化完整 config（含 `configured:true, schemaVersion:2`）经 `AtomicFile` 落盘 → 在内存把站点注册进 WebTabService → 提示「去各标签页登录，准备好后点开始专注」（复用现有 banner 机制，`_focus.Enter()` 时机与现状完全相同）。

不采用「边改边落盘」：任何中途强杀都只留下旧 config 或完整新 config，无中间态（AtomicFile 语义现成的）。wizard 中途退出 app → 下次启动重新进 wizard（幂等）。

### ADR-3 站点数据驱动：`BuiltInSites` 目录 + config `sites[]`

**决策**：
- 新文件 `Services/SiteCatalog.cs`：`SiteDef` record（`Id, Title, Url, WhitelistDomains[], LoginDomains[], AllowMulti`）+ `BuiltInSites` 静态目录（bili/chatgpt/aistudio/deepseek 4 个 preset，域名从现有默认 whitelist/loginDomains 按归属拆分）。
- config 新增 `"sites"`：`[{id:"bili"} | {id:"custom-x", title, url, domains[]}]`。preset 引用只存 id（域名真相在代码目录里，升级 app 可修域名）；custom 自含全部字段。
- **运行时展开**：启动时 `sites[] → 解析成 (id,title,url)` 列表喂给 `RegisterTab` + 合并所有 preset/custom 的 WhitelistDomains→`cfg.Whitelist`、LoginDomains→`cfg.LoginDomains` 喂给 UrlFilter（逻辑在 `SiteCatalog.Resolve(cfg)` 纯函数，可单测）。
- **custom 域名规则（对用户不可见）**：输入 URL → 取 `uri.Host` 去 `www.` 前缀 → 该 host 即 whitelist 域（`EndsWith("."+d)` 天然覆盖子域）→ 拒绝非 http(s)、不可解析 host、与已有域重复（host 相同或互为后缀）。**不猜 OAuth 登录域**：custom 站登录跳转被拦时显示现有的 BlockedBar 提示——这是刻意的克制，不把 loginDomains 概念暴露给用户；真踩坑再按用户反馈加（Later）。
- **多开语义收窄**：`AllowMulti=true` 仅 preset（现状语义：B站×3、GPT×2）；custom 站单例 tab（关闭后从「+」菜单可重开，不重排 id）。理由：动态站点的多开 id 管理（`bili-2` 式）在配置驱动后要处理持久化/去重/删站残留，复杂度不值得。
- **删除/改站的运行时防护**：Frozen 后 sites 不变，所以不存在「运行中站点消失」；「+」菜单与首页快捷入口全部从 ResolvedSites 动态生成，硬编码 QuickSites[] 删除。

### ADR-4 Installed/Portable 双布局，`Paths` 单点分流

**决策**：
```csharp
// Paths.cs 重构（唯一改动点）
DataDir = File.Exists(Path.Combine(BaseDir, "portable.flag"))
    ? Path.Combine(BaseDir, "focus-desktop-data")                                   // portable：现状逐字节保留
    : Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "focus-desktop")
```
- release 构建流程在 portable zip 根目录放空文件 `portable.flag`；Inno 安装包不放 → installed 自动走 LocalAppData。**检测机制=文件存在性**，无注册表、无编译开关，两种产物出自同一份二进制。
- WebView2 UserDataFolder 跟随 DataDir（现状已是 `Paths.DataDir/WebViewProfile`），installed 自动落 LocalAppData，符合 WebView2 规范。
- csproj 删掉「便携布局」注释，`<Version>` 升到 1.0.0，加 `<ApplicationIcon>`（笔图标）与 AssemblyInfo（Product=Focus Desk、描述、版权 MIT Kevin）。
- **LocalAppData 名继续叫 `focus-desktop`**：孤儿 webview 清理脚本的 CommandLine 过滤串不变。

### ADR-5 安装器：Inno Setup 6（免费、稳、脚本即代码）

**决策**：单 `installer/focus-desktop.iss` 脚本进 git，产出 `FocusDesktop-Setup.exe`：
- 装到 `{autopf}\focus-desktop`（即 `C:\Program Files\focus-desktop`）；
- 快捷方式：桌面「Focus Desk」+ 开始菜单（含「恢复」入口 `--restore`，与现状三快捷方式语义一致）；
- **Upgrade**：默认语义即保留 LocalAppData（Inno 从不主动删用户目录）→ 配置与 WebView 登录态天然保留；
- **Uninstall**：`[Code]` 段 `CurUninstallStepChanged(usPostUninstall)` 弹确认（是/否）→ 删 `%LOCALAPPDATA%\focus-desktop` 整目录（config+WebViewProfile）——**必须**在卸载时清掉 configured 标志，否则重装后 wizard 不出现，与「重配=重装」承诺矛盾；**绝不触碰** config 里的 StudyFolder 指向（卸载代码根本不读它，只删自己的数据目录）；
- 检测 WebView2 Runtime：缺失时安装结束页提示并打开官方 bootstrapper 链接（不捆绑下载器，保持安装包小与可审计）；
- Portable.zip 继续发：release 目录原样打包 + `portable.flag` + 内置恢复快捷方式说明。Kevin 自己的机器继续用 portable 布局，现有快捷方式/测试全不炸。
- 图标：`assets/focus.ico`「笔」主题，多尺寸（16/32/48/256）——先行任务，Pencil 单元素 SVG→ICO（ImageMagick 或在线生成一次入库）。
- 本机未装 Inno（已实测）：执行阶段装一次 `winget install JRSoftware.InnoSetup`（或官网安装包），属环境一次性动作。

### ADR-6 背景图：复制入库 + DecodePixelWidth + 静态暗化罩

**决策**：
- wizard 选图 → **复制**进 `DataDir/assets/bg.jpg`（原图留在用户处，之后删/移不受影响——解决「文件丢失 fallback」）；
- 加载：`BitmapImage` + `DecodePixelWidth=2560`（按 200% DPI 物理宽上限），`Freeze()`；>50MB 或解码失败 → 静默回退纯色现状。不做缩略图缓存（单图单用途）。
- 渲染（WPF 天性即 cover）：HomeView 根 Grid 最底层放 `Image Stretch=UniformToFill, StretchDirection=Both, Opacity≈0.22`，其上叠 `Border Background=#99000000`（约 40% 黑罩），所有现有内容天然在其上。两者都是静态 Brush，无动画无每帧合成开销。居中裁切 = UniformToFill 的固有语义（多余部分两侧/上下裁掉，从中心）。
- 文件页/网页页**不铺背景**（可读性优先；网页层本来就被 airspace 占满）。
- config 只存文件名 `"backgroundImage": "bg.jpg"`，不存绝对路径。

### ADR-7 明确 Later（本次不做，写进 README/plan 防 scope creep）

自定义快捷键、配置导入导出、custom 站 OAuth 登录域向导、背景图画廊/多图轮播、自动更新、MSIX/商店签名（先做 Inno 未签名，SmartScreen 提示写进 README FAQ）、多显示器锁定强化、统计/ streak、网页版式主题系统、卸载问卷。自动更新确认不做（用户明示）。

---

## 2. 目标数据流 / 配置生命周期

```
[Installed 首次启动]
  Paths.DataDir = %LOCALAPPDATA%\focus-desktop（不存在→建）
  config 缺失 → MainWindow 显示 SetupWizard（未锁定）
    Step1 学习目录（FolderBrowserDialog，默认 Documents\focus）
    Step2 站点：preset 勾选卡 + custom 添加（URL 输入→校验→卡片）
    Step3 专注语 / 退出语 / 番茄钟四参数
    Step4 背景图（可选，实时预览 cover 效果）
    ── 可随时「上一步」；「预览首页」关 wizard 看效果 ──
    「完成并开始使用」→ AtomicFile 写 config{schemaVersion:2, configured:true, sites[...]}
      → 站点注册+tab条构建 → 登录引导 banner（现有机制）→ 开始专注 → _focus.Enter()
[之后每次启动] configured==true → 直接锁定，wizard 永不出现
[Upgrade] 覆盖 exe → LocalAppData 原样 → 配置/登录态全保留（schemaVersion 旧则迁移器跑）
[Uninstall] Inno 删 Program Files → 确认后删 %LOCALAPPDATA%\focus-desktop → 重装=全新 wizard
```

配置 schema v2（增量字段，旧字段全保留）：
```json
{
  "schemaVersion": 2, "configured": true,
  "studyFolder": "...", "exitPhrase": "...", "focusQuote": "...",
  "pomodoroWorkMinutes": 25, "pomodoroShortBreakMinutes": 5,
  "pomodoroLongBreakMinutes": 15, "pomodoroCyclesUntilLong": 4,
  "sites": [{"id":"bili"}, {"id":"chatgpt"}, {"id":"custom-1","title":"Notion","url":"https://notion.so","domains":["notion.so"]}],
  "backgroundImage": "bg.jpg",
  "whitelist": [...], "loginDomains": [...]   // 仍保留：运行时由 SiteCatalog.Resolve 重算覆盖；legacy 配置靠它们兜底
}
```

---

## 3. 分阶段 Implementation Plan

### Phase 0（收尾阶段统一提交，非前置阻塞）
- 现有两处未提交修复（TabIdOf 崩溃 + 文件列表前景色 + Win32Probe）与后续各 Phase 分支产物**攒到最后一次性提交**（Kevin 指示 2026-09-01），`tests/verify-results.txt` 届时一并加进 .gitignore。
- 开发期基线保护改为本地 tag：`git tag baseline-v0.5.4-fixes` 打在 cf24024，任何阶段回归失败可快速 diff/回退，不依赖 commit。
- 风险与对策：多阶段未提交改动混在工作区 → 每个 Phase 开始前 `git stash list`/`git diff --stat` 核对改动面只含本阶段文件；T2（MainWindow 大改）前对工作区做一次 `git diff > backups/` 快照。

### Phase 1（数据驱动地基，无 UI 变化）
**T1 SiteCatalog + AppSettings v2 字段 + Resolve 纯函数**（新文件 `Services/SiteCatalog.cs`，改 `AppSettings.cs`）
**T2 MainWindow 站点接线改造**：删 3 处硬编码 RegisterTab + QuickSites[] + 4 个固定首页按钮 → 全部从 ResolvedSites 动态生成（首页快捷入口改 ItemsControl/循环建按钮；「+」菜单同数据源）。**MainWindow.xaml.cs 仅此一处大改，锁定层不碰。**
**T3 `--urltest` 扩展**：用例改为经 SiteCatalog.Resolve 的 preset 展开域 + custom 校验用例（合法/重复/子域/非 http/空 host），总数 ≥ 现状 20。
- 验收：默认配置（无 sites 字段=legacy）行为与 v0.5.4 逐像素一致；verify-tabs 13/13；--urltest 全绿；--smoke 绿；verify-step12 13/13。

### Phase 2（Wizard UI，纯新增文件 + App.xaml.cs 两处接线）
**T4 `SetupWizard.xaml/.cs` 四步向导**（含各步校验、预览、原子提交、深色系显式 Foreground）
**T5 首页背景渲染**（HomeView 底层 Image+暗罩 + wizard 选图复制入库 + fallback）
**T6 新测试 `tests/verify-setup.ps1`**：UIA 驱动 wizard 全流程（填完→提交→断言 config 落盘且 configured=true→重启→断言不再进 wizard→站点 tab 数与配置一致）；原子性用例（wizard 中途杀进程→config 不出现半成品）。
- 验收：新套件全绿 + Phase1 全部回归仍绿。

### Phase 3（发布工程）
**T7 `Paths` 双布局 + portable.flag 机制**（含把 Paths 改成可注入/可测试的形态，但保持现有引用点签名不变）
**T8 图标资产 + csproj 元数据**（`assets/focus.ico`「笔」主题单元素几何设计——Kevin 授权自行定稿：深色底可读的简洁钢笔/铅笔形，出 2-3 候选后按 32px 任务栏实尺辨识度选定，多尺寸 16/32/48/256 一次入库；ApplicationIcon，Version 1.0.0，Assembly 信息）
**T9 `installer/focus-desktop.iss` + 构建脚本 `installer/build-release.ps1`**（publish→portable.zip+flag→ISCC 编译→两产物落 release/）
**T10 新测试 `tests/verify-install.ps1`**：静默装（`/VERYSILENT`）→ 断言 Program Files 文件、快捷方式、注册表卸载项 → 首跑断言写 LocalAppData → 覆盖装（upgrade）断言 config 保留 → 卸载断言 LocalAppData 清、学习目录原样（先造一个假学习目录放文件，卸载后断言还在）。
- 验收：T10 全绿；portable 模式跑 verify-step12 仍 13/13（证明双布局没碰锁层）。

### Phase 4（产品化收尾，串行最后）
**T11 README 重写**（上半：效果截图/下载/安装/首次配置/安全退出；下半：架构/测试/恢复机制）+ Release v1.0.0（Setup.exe + Portable.zip + 图标后的截图更新）。
- 验收：Kevin 走一遍「普通用户视角」的 README 流程实测。

---

## 4. Subagent 任务拆分与依赖

```
Phase0: Kevin/Hermes 主会话（提交基线）                ─┐
                                                        ├─ 串行主干
T1 ─┐                                                   │
    ├─→ T2 ──→ T4 ──→ T5 ──→ T7 ──→ T8/T9 ──→ T10 ──→ T11
T3 ─┘        （T4 完成后 T5 可与 T7 之前的回归并行验证） │
```

| 任务 | 模型档位 | 可并行性 | 理由 |
|---|---|---|---|
| T1 SiteCatalog | 强模型 | 与 T3 并行 | 状态机+域名归一化是本次最容易埋雷的逻辑，需仔细设计 Resolve 纯函数与 legacy 兼容 |
| T2 MainWindow 接线 | 强模型（主会话做） | 串行依赖 T1 | 碰 1124 行核心文件，紧邻锁定层接线；风险最高的改动，不建议外包 |
| T3 urltest 扩展 | 普通 coding agent | 与 T1/T2 并行（只动 Tests/UrlSelfTest.cs，需 T1 接口先定） | 接口定后是纯用例编写 |
| T4 Wizard UI | 普通 coding agent（给足设计稿级 spec） | T2 后可启动，与 T5 并行（不同文件；T5 只碰 HomeView XAML 区域） | 纯新增文件，隔离性好；需附深色主题/显式 Foreground/FolderBrowserDialog 规范 |
| T5 背景渲染 | 普通 coding agent | 与 T4 并行 | 改动面小（XAML 一处 + 加载 helper） |
| T6 verify-setup.ps1 | 强模型 | T4+T5 后 | UIA/像素验证经验丰富与否直接决定脚本可信度 |
| T7 Paths 双布局 | 强模型（主会话 review） | T6 绿之后 | 一行分流牵动所有数据落点与测试脚本假设 |
| T8 图标+元数据 | 普通 coding agent | 与 T7 并行 | 资产任务 |
| T9 Inno 脚本 | 普通 coding agent（模板成熟） | 依赖 T7/T8 | Inno DSL 有标准范式，给本 plan 的 ADR-5 即可 |
| T10 verify-install.ps1 | 强模型 | 依赖 T9 | 装/升/卸三态断言，必须真机跑通 |
| T11 README/Release | 强模型（主会话） | 最后串行 | 产品口径与截图，Kevin 过目 |

**核心文件并发红线**：`MainWindow.xaml.cs` 同一时刻只允许一个任务持有（T2 之后任何任务不再改它，wizard 通过事件/方法与它对接）；`App.xaml.cs` 仅 T4 接线时改一次（wizard 显示分支）。T4/T5 并行时约定：T5 只改 `MainWindow.xaml` 的 HomeView 区域 + 新 helper 文件，不碰 `.cs`。

---

## 5. Installer / Release 方案（ADR-5 展开）

产物（GitHub Release v1.0.0）：
- `FocusDesktop-Setup.exe`（Inno，x64，自包含 .NET，~150MB 级）：安装/升级/卸载/快捷方式/恢复入口/WebView2 检测提示。
- `focus-desktop-portable.zip`：exe + `portable.flag` + README 便携说明。Kevin 自用与「不想装」用户。
- 图标：先行 T8（「笔」主题，简洁几何形，深色底可读）。
- 发布纪律沿用：Release 列表只留最新 patch、保里程碑；README 与二进制同轮更新；未签名 SmartScreen 提示写 FAQ。
- 本机装 Inno 是执行期一次性环境动作（winget），不属于代码变更。

---

## 6. 风险登记

**P0（可能毁掉现有稳定成果）**
1. MainWindow 站点接线（T2）引入 tab 生命周期回归 → 缓解：T2 单独一阶段，验收=verify-tabs+step12+像素级切换抽查全绿后才放行 T4；改前 git tag 基线。
2. Paths 分流（T7）让 portable 模式数据落点漂移，watchdog/恢复/清孤儿脚本静默失效 → 缓解：portable.flag 存在性检测+T7 验收强制 portable 跑全量 step12；清理过滤串 `focus-desktop-data` 不变。
3. Wizard 原子提交写坏 config 导致锁定层读到半成品 → 缓解：AtomicFile 现成语义 + T6 杀进程用例 + `LoadOrDefault` 损坏回退默认（回退后 configured 缺失=重新进 wizard，安全方向失败）。

**P1（功能正确性）**
4. custom 站域名归一化漏洞（`evil.com` 当 `bilibili.com` 后缀之类的反向问题不存在——我们是白名单内后缀放宽；真风险是用户输 `https://bilibili.com.evil.com` 被判重复/放行）→ 缓解：归一化只取 host、比较用 `==` 与 `EndsWith(".")` 双向检查重复，T3 用例覆盖。
5. Legacy 配置（Kevin 自己的）被误判 Draft 弹 wizard → 缓解：schemaVersion 缺失+configured 缺失但 whitelist 存在 → Legacy Frozen；T6 含 legacy config 用例（拿 Kevin 现行 config.json 做 fixture）。
6. Wizard 中 FolderBrowserDialog 在 kiosk 全屏下的层级问题（未锁定态无 airspace 问题，但预览态窗口样式不同）→ 缓解：T6 实机 UIA 跑通；dialog owner 显式设为 MainWindow。
7. 大背景图内存（50MB 原图 decode）→ 缓解：DecodePixelWidth + 文件大小预检 + 解码 try/catch 回退。

**P2（体验/发布）**
8. Inno 未签名 SmartScreen 拦截 → README FAQ + 后续按用户量考虑签名（Later）。
9. 笔图标审美 → Kevin 已授权自行定稿：主笔场景实测 32px 小尺寸辨识度后选定，不送审。
10. WebView2 Runtime 缺失的新用户首跑 → 安装器结束页提示 + 现有 ShowWebErrorCard 兜底已够。

**Kevin 可能遗漏、但影响 public release 的点（已在 plan 覆盖，单独点名）**：
- **WebView2 Runtime 前置**：普通用户机器大多有（Win11 自带），但纯净系统没有——安装器检测+引导已含（T9）。
- **卸载必须清 configured**：否则「重装=重配」承诺破产（T9 卸载脚本已含）。
- **学习目录绝不进安装/卸载任何删除路径**（T10 显式断言）。
- **旧配置迁移**：Kevin 自用 config 是 v1 无 schemaVersion——Legacy Frozen 分支保证他自己升级后零感知（T6 fixture）。
- **快捷方式工作目录**：installed 快捷方式 WorkingDirectory 必须设安装目录，否则 watchdog 相对路径/日志落点异常（T9）。

---

## 7. 执行顺序（一句话版）

打本地基线 tag → T1（强）∥ T3（普通） → T2（主会话，全回归） → T4（普通）∥ T5（普通） → T6（强，全回归） → T7（强）∥ T8（普通） → T9（普通） → T10（强，真机装升卸） → T11（主会话，README+Release，Kevin 终审） → 全部改动统一一次提交+推送（Kevin 点名后）。

全程规矩不变：commit/push 攒到最后统一做（Kevin 指示）；每阶段绿灯才进下一阶段；新症状紧跟自己改动=先查自己回归。
