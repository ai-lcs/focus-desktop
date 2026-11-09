; focus-desktop Inno Setup 脚本（Public v1）
; 构建：installer/build-release.ps1 调 ISCC 编译本脚本。
; 产物：FocusDesktop-Setup-<version>.exe
; 关键语义：
;  - 新版独立装到 {autopf}\FocusDeskDock；运行数据落 %LOCALAPPDATA%\focus-desktop-dock
;  - 升级：仅新版自身固定 AppId，不复用旧版身份；拒绝覆盖未标记或便携目录
;  - 卸载：清除当前账户的应用数据；由应用证明账户归属后清除
;           清理由持有桌面与数据目录锁的应用 helper 完成；身份或锁无法确认时中止清除
;  - 快捷方式：桌面 + 开始菜单；「恢复」入口指向 --restore

#define AppName "Focus Desk"
#define AppNameZh "专注学习环境"
#define AppExeName "focus-desktop.exe"
#ifndef Version
#define Version "2.0.0"
#endif
#ifndef PayloadDir
#define PayloadDir "payload"
#endif
#define Publisher "Kevin Li (ai-lcs)"

[Setup]
AppId={{50D19490-82AC-42D3-95B2-F8EBEA860817}
AppName={#AppName}
AppVersion={#Version}
AppVerName={#AppName} {#Version}
AppPublisher={#Publisher}
AppPublisherURL=https://github.com/ai-lcs/focus-desktop
AppSupportURL=https://github.com/ai-lcs/focus-desktop/issues
DefaultDirName={autopf}\FocusDesk
DefaultGroupName={#AppName}
UsePreviousGroup=no
DisableProgramGroupPage=yes
OutputDir=..\release\
OutputBaseFilename=FocusDesk-Setup-{#Version}
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
Source: "dock-install.identity"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Comment: "{#AppNameZh}"
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Comment: "{#AppNameZh}"
Name: "{group}\{#AppName} 恢复"; Filename: "{app}\{#AppExeName}"; Parameters: "--restore"; WorkingDir: "{app}"; Comment: "任务栏丢失/异常退出后双击恢复"

[InstallDelete]
Type: files; Name: "{autodesktop}\Focus Desk Dock.lnk"
Type: files; Name: "{autoprograms}\Focus Desk Dock\Focus Desk Dock.lnk"
Type: files; Name: "{autoprograms}\Focus Desk Dock\Focus Desk Dock 恢复.lnk"
Type: dirifempty; Name: "{autoprograms}\Focus Desk Dock"

[Run]
; 让实际运行账户的程序判断是否首次配置，不能用提权 Setup 账户的 LocalAppData 代替。
; 不使用 postinstall：它只是完成页上的可选复选框，用户取消后不会启动首次配置。
Filename: "{app}\{#AppExeName}"; Parameters: "--first-run-if-needed"; Flags: nowait runasoriginaluser

[UninstallDelete]
; 这里只清理安装目录中的程序文件；用户数据由持锁 helper 处理。
Type: files; Name: "{app}\{#AppExeName}"
Type: files; Name: "{app}\focus-desktop-watchdog.exe"

[Code]

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\portable.flag')) or
     (FileExists(ExpandConstant('{app}\{#AppExeName}')) and
      (not FileExists(ExpandConstant('{app}\dock-install.identity')))) then
    Result := '此目录包含旧版或便携版 Focus Desk。新版不会覆盖它，请选择独立的新目录。';
end;

function HasUninstallParam(const ParamName: String): Boolean;
begin
  Result := Pos('/' + Uppercase(ParamName), Uppercase(GetCmdTail)) > 0;
end;

function IsUnattendedUninstall(): Boolean;
begin
  Result := HasUninstallParam('VERYSILENT') or HasUninstallParam('SUPPRESSMSGBOXES');
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
  Executed: Boolean;
  ErrorMessage: String;
  PrepareArguments: String;
begin
  // 不使用 [UninstallRun]：它会等待进程，但不能在 Pascal 事件中据其退出码中止卸载。
  // 用户数据若获准清除，必须由 helper 持有桌面锁和数据目录锁完成；Inno 不在 helper 退出后清目录。
  PrepareArguments := '--prepare-uninstall --remove-user-data';
  Executed := Exec(
      ExpandConstant('{app}\{#AppExeName}'),
    PrepareArguments,
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  if (not Executed) or (ResultCode <> 0) then
  begin
    if Executed then
    begin
      if ResultCode = 2 then
        ErrorMessage := '用户数据清理未完成，可能已部分清除。卸载已中止，请检查数据目录并从自己的备份恢复。'
      else
        ErrorMessage := 'Focus Desk 未能验证当前账户的数据归属，或数据仍在使用，无法完成清理（退出代码 ' + IntToStr(ResultCode) + '）。';
    end
    else
      ErrorMessage := '无法运行 Focus Desk 卸载准备程序：' + SysErrorMessage(ResultCode);
    Log(ErrorMessage + ' 未继续删除安装文件。');
    if not IsUnattendedUninstall() then
      MsgBox(ErrorMessage + #13#10 + '未继续删除安装文件。请先核对桌面和数据状态后重试。', mbError, MB_OK);
    Result := False;
    exit;
  end;

  Result := True;
end;
