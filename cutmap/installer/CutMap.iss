#ifndef Stage
  #error Stage must point to a self-contained published directory
#endif
#ifndef AppVersion
  #define AppVersion "0.1.3"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif
#ifdef TestBuild
  #define ProductId "CutMapAcceptance"
  #define ProductName "CutMap 验证版"
  #define OutputName "CutMap-" + AppVersion + "-Validation-Setup-win-x64"
#else
  #define ProductId "CutMap"
  #define ProductName "CutMap"
  #define OutputName "CutMap-" + AppVersion + "-Setup-win-x64"
#endif

[Setup]
AppId={#ProductId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppPublisher=CutMap
DefaultDirName={localappdata}\Programs\{#ProductId}
DefaultGroupName={#ProductName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
SetupIconFile=..\assets\cutmap.ico
UninstallDisplayIcon={app}\CutMap.App.exe
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#Stage}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#ProductName}"; Filename: "{app}\CutMap.App.exe"
Name: "{autodesktop}\{#ProductName}"; Filename: "{app}\CutMap.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CutMap.App.exe"; Description: "运行 CutMap"; Flags: nowait postinstall skipifsilent

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Target: String;
  FindRec: TFindRec;
begin
  Result := '';
  Target := ExpandConstant('{app}');
  if (Length(Target) <= 3) or (ExtractFileDir(Target) = Target) then
  begin
    Result := '不能安装到磁盘根目录。';
    exit;
  end;
  if DirExists(Target) and not FileExists(Target + '\build-info.json') then
  begin
    if FindFirst(Target + '\*', FindRec) then
    begin
      try
        repeat
          if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
          begin
            Result := '目标目录包含非 CutMap 文件，请选择空目录。';
            exit;
          end;
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;
  end;
end;
