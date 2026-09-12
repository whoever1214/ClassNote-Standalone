; ================================================================
; ClassNote 单机版 — Windows 安装脚本 (Inno Setup 6)
; 产物: dist\ClassNote-1.0.0-setup-x64.exe (自包含 x64, 免装 .NET)
; 用法: ISCC.exe installer\ClassNote.iss
; ================================================================
#define MyAppName "ClassNote 课堂笔记"
#define MyAppNameEn "ClassNote"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "ClassNote"
#define MyAppExeName "ClassNote.exe"

[Setup]
AppId={{0320E40B-A535-4C7D-A3FB-DC6991326D04}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} 安装程序
; 自包含 x64 应用, 按当前用户安装(免管理员), 安装目录可写(WebView2 运行库同目录生成)
DefaultDirName={localappdata}\Programs\ClassNote
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=ClassNote-{#MyAppVersion}-setup-x64
SetupIconFile=..\ClassNote\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
ChangesAssociations=no

[Languages]
Name: "chs"; MessagesFile: "ChineseSimplified.isl"
Name: "cht"; MessagesFile: "ChineseTraditional.isl"
Name: "en";  MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加任务:"

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 注意: 不随附 {#MyAppNameEn}.exe.WebView2 目录 —— 该目录为运行时自动生成的用户配置缓存

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
// 检测 WebView2 Evergreen 运行时是否已安装(机器级 32/64 位视图 + 用户级)
function IsWebView2Installed(): Boolean;
var
  V: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V) then
    Result := True
  else if RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V) then
    Result := True
  else if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V) then
    Result := True;
end;

function InitializeSetup(): Boolean;
var
  Msg: String;
begin
  Result := True;
  if not IsWebView2Installed() then
  begin
    Msg := '未检测到 Microsoft Edge WebView2 运行时，笔记预览页面可能无法正常显示。' + Chr(13) + Chr(10) +
           '请访问 https://developer.microsoft.com/microsoft-edge/webview2/ 下载安装。';
    MsgBox(Msg, mbInformation, MB_OK);
  end;
end;
