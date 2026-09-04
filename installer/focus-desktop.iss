; focus-desktop Inno Setup 脚本（Public v1）
; 构建：installer/build-release.ps1 调 ISCC 编译本脚本。
; 产物：FocusDesktop-Setup-<version>.exe
; 关键语义：
;  - 装到 {autopf}\FocusDesk（Program Files）；无 portable.flag → 运行数据落 %LOCALAPPDATA%\focus-desktop
;  - 升级：覆盖装（AppId 固定），LocalAppData 数据天然保留
;  - 卸载：开始前明确询问，默认保留 %LOCALAPPDATA%\focus-desktop；仅确认后删除；绝不触碰 StudyFolder
;  - 快捷方式：桌面 + 开始菜单；「恢复」入口指向 --restore

#define AppName "Focus Desk"
#define AppNameZh "专注学习环境"
#define AppExeName "focus-desktop.exe"
#ifndef Version
#define Version "1.1.3"
#endif
#define Publisher "Kevin Li (ai-lcs)"

[Setup]
AppId={{7B3F9E2A-6C1D-4E8B-9F5A-F2D8C4A1E6B0}
AppName={#AppName}
AppVersion={#Version}
AppVerName={#AppName} {#Version}
AppPublisher={#Publisher}
AppPublisherURL=https://github.com/ai-lcs/focus-desktop
AppSupportURL=https://github.com/ai-lcs/focus-desktop/issues
DefaultDirName={autopf}\FocusDesk
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\release\
OutputBaseFilename=FocusDesktop-Setup-{#Version}
SetupIconFile=..\assets\focus.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
; 允许命令行覆盖权限级别（T10 静默自动化：/CURRENTUSER 装到用户目录免 UAC；
; 交互安装默认 admin 装 Program Files）
PrivilegesRequiredOverridesAllowed=commandline
; 未签名：SmartScreen 会提示（README FAQ 已说明）
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExeName}
; 中文安装向导（单语种应用，不弹语言选择框）
ShowLanguageDialog=no

[languages]
; 简中语言文件（MIT，kira-96 翻译）随仓库携带（installer/ChineseSimplified.isl）——
; Inno 官方安装包不带中文，CI/他机构建不再依赖下载
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"

[Files]
; publish 产物由 build-release.ps1 先输出到 installer\payload\
Source: "payload\focus-desktop.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "payload\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Comment: "{#AppNameZh}"
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Comment: "{#AppNameZh}"
Name: "{group}\{#AppName} 恢复"; Filename: "{app}\{#AppExeName}"; Parameters: "--restore"; WorkingDir: "{app}"; Comment: "任务栏丢失/异常退出后双击恢复"

[Run]
; 仅全新安装自动启动首次配置。升级安装保留数据且不自动进入专注模式。
; 不使用 postinstall：它只是完成页上的可选复选框，用户取消后不会启动首次配置。
Filename: "{app}\{#AppExeName}"; Flags: nowait runasoriginaluser skipifsilent; Check: ShouldLaunchFirstRun

[UninstallRun]
; 先恢复任务栏/会话标志并结束主程序与看门狗，避免卸载后旧实例继续存活。
Filename: "{app}\{#AppExeName}"; Parameters: "--prepare-uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "PrepareFocusDeskUninstall"

[UninstallDelete]
; 安装目录始终清理；用户数据在 [Code] 的卸载运行阶段按明确选择删除。
Type: files; Name: "{app}\{#AppExeName}"
Type: files; Name: "{app}\focus-desktop-watchdog.exe"

[Code]
const
  LocalDataDir = 'focus-desktop';

var
  RemoveUserData: Boolean;
  FreshDataAtInstall: Boolean;

function LocalAppDataPath(): String;
begin
  Result := ExpandConstant('{localappdata}');
end;

function HasUninstallParam(const ParamName: String): Boolean;
begin
  Result := Pos('/' + Uppercase(ParamName), Uppercase(GetCmdTail)) > 0;
end;

function IsUnattendedUninstall(): Boolean;
begin
  Result := HasUninstallParam('VERYSILENT') or HasUninstallParam('SUPPRESSMSGBOXES');
end;

function InitializeSetup(): Boolean;
begin
  FreshDataAtInstall := not DirExists(AddBackslash(LocalAppDataPath()) + LocalDataDir);
  Result := True;
end;

function ShouldLaunchFirstRun(): Boolean;
begin
  Result := FreshDataAtInstall;
end;

function InitializeUninstall(): Boolean;
begin
  // 无人值守卸载默认保留；显式参数清除；正常卸载在任何删除动作前先询问。
  RemoveUserData := False;
  if HasUninstallParam('REMOVEUSERDATA') then
    RemoveUserData := True
  else if not IsUnattendedUninstall() then
    RemoveUserData := MsgBox(
      '是否同时删除用户数据（配置、网站登录态、背景图）？' + #13#10 +
      '选择「是」= 彻底清理（重新安装会重新出现首次配置向导）；' + #13#10 +
      '选择「否」= 保留（重新安装后沿用现有配置）。',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if (CurUninstallStep = usPostUninstall) and RemoveUserData then
  begin
    DataDir := AddBackslash(LocalAppDataPath()) + LocalDataDir;
    if DirExists(DataDir) then
      DelTree(DataDir, True, True, True);
  end;
end;
