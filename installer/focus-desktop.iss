; focus-desktop Inno Setup 脚本（Public v1）
; 构建：installer/build-release.ps1 调 ISCC 编译本脚本。
; 产物：FocusDesktop-Setup-<version>.exe
; 关键语义：
;  - 装到 {autopf}\FocusDesk（Program Files）；无 portable.flag → 运行数据落 %LOCALAPPDATA%\focus-desktop
;  - 升级：覆盖装（AppId 固定），LocalAppData 数据天然保留
;  - 卸载：先明确询问数据处理，再要求应用恢复任务栏并成功退出；失败则中止卸载
;           默认清除 %LOCALAPPDATA%\focus-desktop（重装=重新走首次配置）；绝不触碰 StudyFolder
;  - 快捷方式：桌面 + 开始菜单；「恢复」入口指向 --restore

#define AppName "Focus Desk"
#define AppNameZh "专注学习环境"
#define AppExeName "focus-desktop.exe"
#ifndef Version
#define Version "1.2.0"
#endif
#ifndef PayloadDir
#define PayloadDir "payload"
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
Source: "{#PayloadDir}\focus-desktop.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Comment: "{#AppNameZh}"
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Comment: "{#AppNameZh}"
Name: "{group}\{#AppName} 恢复"; Filename: "{app}\{#AppExeName}"; Parameters: "--restore"; WorkingDir: "{app}"; Comment: "任务栏丢失/异常退出后双击恢复"

[Run]
; 仅全新安装自动启动首次配置。升级安装保留数据且不自动进入专注模式。
; 不使用 postinstall：它只是完成页上的可选复选框，用户取消后不会启动首次配置。
Filename: "{app}\{#AppExeName}"; Flags: nowait runasoriginaluser; Check: ShouldLaunchFirstRun

[UninstallDelete]
; 安装目录始终清理；用户数据在 [Code] 的卸载运行阶段按明确选择删除。
Type: files; Name: "{app}\{#AppExeName}"
Type: files; Name: "{app}\focus-desktop-watchdog.exe"

[Code]
const
  LocalDataDir = 'focus-desktop';

var
  RemoveUserData: Boolean;

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

function ShouldLaunchFirstRun(): Boolean;
begin
  // 直接在 [Run] 真正执行时检查，不能依赖初始化阶段缓存的全局变量。
  Result := not DirExists(AddBackslash(LocalAppDataPath()) + LocalDataDir);
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
  Executed: Boolean;
  ErrorMessage: String;
begin
  // 正常卸载默认彻底清理（本应用设计约定：重装=重新走首次配置向导，见 README）。
  // 无人值守（VERYSILENT/SUPPRESSMSGBOXES）保持安全默认：保留数据；显式 /REMOVEUSERDATA 强制清除。
  RemoveUserData := False;
  if HasUninstallParam('REMOVEUSERDATA') then
    RemoveUserData := True
  else if not IsUnattendedUninstall() then
    RemoveUserData := MsgBox(
      '卸载并删除全部用户数据（配置、网站登录态）？' + #13#10 +
      '「是」= 彻底清理（默认。重新安装会重新出现首次配置向导）；' + #13#10 +
      '「否」= 仅卸载程序、保留数据（重新安装后沿用现有配置）。',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDYES;

  // 不使用 [UninstallRun]：它会等待进程，但不能在 Pascal 事件中据其退出码中止卸载。
  // 只有应用确认恢复了桌面并完成清理后，才允许 Inno 删除安装目录或用户数据。
  Executed := Exec(
      ExpandConstant('{app}\{#AppExeName}'),
    '--prepare-uninstall',
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  if (not Executed) or (ResultCode <> 0) then
  begin
    RemoveUserData := False;
    if Executed then
      ErrorMessage := 'Focus Desk 未能安全完成卸载准备（退出代码 ' + IntToStr(ResultCode) + '）。'
    else
      ErrorMessage := '无法运行 Focus Desk 卸载准备程序：' + SysErrorMessage(ResultCode);
    Log(ErrorMessage + ' 卸载已中止，用户数据已保留。');
    if not IsUnattendedUninstall() then
      MsgBox(ErrorMessage + #13#10 + '卸载已中止，用户数据已保留。请先恢复桌面状态后重试。', mbError, MB_OK);
    Result := False;
    exit;
  end;

  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  Deleted: Boolean;
  ErrorMessage: String;
begin
  if (CurUninstallStep = usPostUninstall) and RemoveUserData then
  begin
    DataDir := AddBackslash(LocalAppDataPath()) + LocalDataDir;
    if DirExists(DataDir) then
    begin
      Deleted := DelTree(DataDir, True, True, True);
      if (not Deleted) or DirExists(DataDir) then
      begin
        ErrorMessage := 'Focus Desk 已卸载，但用户数据可能仍有残留，请检查目录：' + DataDir;
        Log(ErrorMessage);
        if not IsUnattendedUninstall() then
          MsgBox(ErrorMessage + #13#10 + '请检查并手动删除残留数据。', mbError, MB_OK);
      end;
    end;
  end;
end;
