; Установщик Maut Vision (Inno Setup 6.5+). Собирается build-installer.ps1, который передаёт пути и версию:
;   ISCC /DAppVersion=1.0.0 /DPublishDir=<публикация> /DWorkerDir=<onedir worker> /DWebView2Bootstrapper=<exe> MautVision.iss
; /DFast — быстрое сжатие для проверок; /DTestInstall — отдельные AppId, имя и папка для автоматической проверки (tools/verify-install.ps1).

#ifndef AppVersion
  #error AppVersion is required (build-installer.ps1 reads it from Directory.Build.props).
#endif
#ifndef PublishDir
  #error PublishDir must point to the published application directory.
#endif
#ifndef WorkerDir
  #error WorkerDir must point to the built Python worker (maut-training-worker onedir).
#endif
#ifndef WebView2Bootstrapper
  #error WebView2Bootstrapper must point to MicrosoftEdgeWebview2Setup.exe (tools/get-webview2.ps1).
#endif

#define AppExe "MautVision.Desktop.exe"
#ifdef TestInstall
  #define AppIdValue "{{6C0C2B8E-3F6B-4C55-9C34-0D5E1B2A7F41}"
  #define AppTitle "Maut Vision (проверка)"
  #define AppFolder "MautVisionTest"
  #define OutputName "MautVision-TestSetup"
#else
  #define AppIdValue "{{CBF35324-4598-4992-BD41-C9A804AE0199}"
  #define AppTitle "Maut Vision"
  #define AppFolder "MautVision"
  #define OutputName "MautVision-Setup-" + AppVersion
#endif

[Setup]
AppId={#AppIdValue}
AppName={#AppTitle}
AppVersion={#AppVersion}
AppVerName={#AppTitle} {#AppVersion}
AppPublisher=Maut Vision
AppPublisherURL=https://github.com/maut-dev/maut-vision
AppSupportURL=https://github.com/maut-dev/maut-vision/issues
AppUpdatesURL=https://github.com/maut-dev/maut-vision/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Maut Vision
VersionInfoDescription=Maut Vision Setup
; Per-user: %LOCALAPPDATA%\Programs\MautVision without administrator rights; the folder can be changed.
DefaultDirName={autopf}\{#AppFolder}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
DisableProgramGroupPage=yes
DisableDirPage=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\src\MautVision.Desktop\Assets\maut-vision.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppTitle}
LicenseFile=..\LICENSE
; The wizard speaks the language of Windows (English or Russian); the choice is asked only when neither matches.
ShowLanguageDialog=auto
OutputBaseFilename={#OutputName}
; The running application is closed through Restart Manager: it exits at WM_QUERYENDSESSION instead of hiding in the tray. AppMutex is not
; used for Setup: its check comes before Restart Manager and would stop a silent update over a running application. The uninstaller
; checks the same mutexes in [Code].
CloseApplications=yes
RestartApplications=no
#ifdef Fast
Compression=lzma2/fast
SolidCompression=yes
LZMANumBlockThreads=4
#else
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
english.InstallingWebView2=Installing Microsoft Edge WebView2 Runtime…
russian.InstallingWebView2=Установка Microsoft Edge WebView2 Runtime…
english.AppDirTooLong=The installation folder path is too long (%1 characters, up to %2 allowed): the files of the training environment will not fit into the Windows path length limit. Select a folder with a shorter path.
russian.AppDirTooLong=Путь к папке установки слишком длинный (%1 символов, допустимо до %2): файлы среды обучения не поместятся в ограничение Windows на длину пути. Выберите папку с более коротким путём.
english.AppRunning=Maut Vision is running. Close it: the icon in the notification area → “Exit”, then press OK.
russian.AppRunning=Maut Vision запущен. Закройте его: значок в области уведомлений → «Выйти», затем нажмите OK.
english.DeleteDataQuestion=Delete projects and data?%n%nThe folder %1 contains projects, images, datasets, models and downloaded weights. Without deletion they stay and open after a new installation.
russian.DeleteDataQuestion=Удалить проекты и данные?%n%nПапка %1 содержит проекты, изображения, датасеты, модели и загруженные веса. Без удаления они останутся и откроются после повторной установки.
english.DeleteDataFailed=Some files in %1 could not be deleted. Delete the folder manually.
russian.DeleteDataFailed=Часть файлов в %1 удалить не удалось. Удалите папку вручную.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; An update replaces these folders entirely: files of the previous version (worker packages, web chunks, runtimes) must not stay.
Type: filesandordirs; Name: "{app}\python"
Type: filesandordirs; Name: "{app}\web"
Type: filesandordirs; Name: "{app}\catalog"
Type: filesandordirs; Name: "{app}\tools\ffmpeg"
Type: filesandordirs; Name: "{app}\runtimes"

[Files]
; First in the archive: extracting it does not unpack the large blocks after it.
Source: "{#WebView2Bootstrapper}"; DestDir: "{tmp}"; DestName: "MicrosoftEdgeWebview2Setup.exe"; Flags: deleteafterinstall; Check: NeedsWebView2
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The worker (≈ 3.6 GB) is its own solid block.
Source: "{#WorkerDir}\*"; DestDir: "{app}\python\maut-training-worker"; Flags: ignoreversion recursesubdirs createallsubdirs solidbreak

[Icons]
Name: "{autoprograms}\{#AppTitle}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppTitle}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "{cm:InstallingWebView2}"; Flags: waituntilterminated; Check: NeedsWebView2
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppTitle}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  WebView2Client = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  RunningMutexes = 'MautVision-Running,Global\MautVision-Running';
  { The deepest worker file (torch licence notices) adds about 170 characters to the folder; Windows paths are limited to 260. }
  MaxAppDirLength = 85;

function AppDirTooLongMessage(Directory: String): String;
begin
  if Length(Directory) > MaxAppDirLength then
    Result := FmtMessage(CustomMessage('AppDirTooLong'), [IntToStr(Length(Directory)), IntToStr(MaxAppDirLength)])
  else
    Result := '';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Message: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    Message := AppDirTooLongMessage(WizardDirValue);
    if Message <> '' then
    begin
      MsgBox(Message, mbError, MB_OK);
      Result := False;
    end;
  end;
end;

{ Also checked here because a silent install skips the folder page. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := AppDirTooLongMessage(ExpandConstant('{app}'));
end;

function WebView2Version(RootKey: Integer; SubKey: String): String;
begin
  if not RegQueryStringValue(RootKey, SubKey, 'pv', Result) then
    Result := '';
end;

function IsWebView2Version(Version: String): Boolean;
begin
  Result := (Version <> '') and (Version <> '0.0.0.0');
end;

{ The Evergreen runtime registers its version (pv) machine-wide (32-bit view) or for the user. }
function NeedsWebView2: Boolean;
begin
  Result := not IsWebView2Version(WebView2Version(HKLM32, 'SOFTWARE\' + WebView2Client))
    and not IsWebView2Version(WebView2Version(HKCU, 'Software\' + WebView2Client));
end;

{ The application cannot be removed while it runs: it holds MautVision-Running. A silent uninstall stops at once (Cancel). }
function InitializeUninstall: Boolean;
begin
  Result := True;
  while CheckForMutexes(RunningMutexes) do
  begin
    if SuppressibleMsgBox(CustomMessage('AppRunning'), mbError, MB_OKCANCEL, IDCANCEL) <> IDOK then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

{ Projects and data of the user (%LOCALAPPDATA%\MautVision) stay unless the user asks; a silent uninstall never removes them. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  DataDir := ExpandConstant('{localappdata}\MautVision');
  if UninstallSilent or not DirExists(DataDir) then
    Exit;
  if MsgBox(FmtMessage(CustomMessage('DeleteDataQuestion'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    if not DelTree(DataDir, True, True, True) then
      MsgBox(FmtMessage(CustomMessage('DeleteDataFailed'), [DataDir]), mbError, MB_OK);
  end;
end;
