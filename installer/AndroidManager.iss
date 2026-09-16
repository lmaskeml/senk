; SeND ANDROID MANAGER — Inno Setup 6
; Derleme:  "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\AndroidManager.iss
; Çıktı:    dist\AndroidManager_Kurulum.exe

#define MyAppName          "SeND ANDROID MANAGER"
#define MyAppVersion       "1.0.0"
#define MyAppPublisher     "SeND"
#define MyAppExeName       "AndroidManager.Shell.exe"
#define MyAppId            "{{A7C3E91F-4B2D-4E18-9F06-8D2A1C5B7E44}"
#define MyOutputName       "AndroidManager_Kurulum"
#define MyDesktopApkName   "AM_Companion_Kurulum.apk"

#define DistRoot       "..\dist"
#define PublishDir     "..\dist\AndroidManager-1.0.0-win-x64-framework"
#define IconFile       "..\src\AndroidManager.Shell\Assets\app.ico"
#define CompanionApk   "..\dist\apk\AndroidManagerCompanion-release.apk"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (C) 2026 {#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} kurulum

DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
AllowNoIcons=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
WizardSizePercent=120
ShowLanguageDialog=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
OutputDir={#DistRoot}
OutputBaseFilename={#MyOutputName}
SetupIconFile={#IconFile}
InfoBeforeFile=KURULUM.txt
InfoAfterFile=BILGILENDIRME.txt
LicenseFile=
SetupLogging=yes

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Messages]
turkish.WelcomeLabel1={#MyAppName} kurulumuna hoş geldiniz
turkish.WelcomeLabel2=Bu sihirbaz [name/ver] uygulamasını bilgisayarınıza kuracaktır.%n%nADB, iOS (libimobiledevice), yansıtma araçları ve kablosuz companion APK kuruluma dahildir.%n%niPhone aktarımı için Apple Mobile Device Support / Apple Devices bir kez kurulmalıdır.%n%nDevam etmeden önce diğer uygulamaları kapatmanız önerilir.
turkish.FinishedLabel={#MyAppName} bilgisayarınıza kuruldu.%n%nMasaüstüne AM_Companion_Kurulum.apk kopyalandı (kablosuz bağlantı için, isteğe bağlı).
turkish.InfoBeforeLabel=Kurulum ve kullanım (çok basit!)
turkish.InfoAfterLabel=Bilgilendirme
turkish.ConfirmUninstall=%1 uygulamasını ve tüm bileşenlerini kaldırmak istediğinize emin misiniz?

[Tasks]
Name: "desktopicon"; Description: "Masaüstü kısayolu oluştur"; GroupDescription: "Ek görevler:"; Flags: checkedonce
Name: "companionapk"; Description: "Companion APK'yi masaüstüne kopyala (AM_Companion_Kurulum.apk)"; GroupDescription: "Ek görevler:"; Flags: checkedonce

[Files]
; Ana uygulama (framework-dependent win-x64)
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,companion\*"

; Kullanım kılavuzu (program klasörü + sihirbaz)
Source: "BILGILENDIRME.txt"; DestDir: "{app}"; DestName: "KURULUM-VE-KULLANIM.txt"; Flags: ignoreversion
Source: "KURULUM.txt"; DestDir: "{app}"; Flags: ignoreversion

; Kablosuz companion — masaüstü + program klasörü yedeği
Source: "{#CompanionApk}"; DestDir: "{commondesktop}"; DestName: "{#MyDesktopApkName}"; Tasks: companionapk; Flags: ignoreversion
Source: "{#CompanionApk}"; DestDir: "{app}\companion"; DestName: "{#MyDesktopApkName}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "Android cihaz yönetimi"
Name: "{group}\Kurulum ve Kullanım"; Filename: "{app}\KURULUM-VE-KULLANIM.txt"; Comment: "Kurulum adımları ve kablosuz companion"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Android Manager"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; Comment: "{#MyAppName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{#MyAppName} uygulamasını başlat"; Flags: nowait postinstall skipifsilent
Filename: "{app}\KURULUM-VE-KULLANIM.txt"; Description: "Kurulum ve kullanım kılavuzunu aç"; Flags: nowait postinstall skipifsilent shellexec unchecked

[Code]
function DotNetDesktop10Installed: Boolean;
var
  SharedDir: String;
  FindRec: TFindRec;
begin
  Result := False;
  SharedDir := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if not DirExists(SharedDir) then
    Exit;
  if FindFirst(SharedDir + '\10.*', FindRec) then
  begin
    try
      repeat
        if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function InitializeSetup: Boolean;
var
  ErrCode: Integer;
begin
  Result := True;
  if not DotNetDesktop10Installed then
  begin
    if MsgBox(
         'Bu kurulum .NET 10 Desktop Runtime (x64) ister.'#13#10#13#10 +
         'Bilgisayarda bulunamadı. İndirme sayfasını şimdi açmak ister misiniz?'#13#10#13#10 +
         'Runtime kurmadan program açılmayabilir.',
         mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open',
        'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe',
        '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DeleteFile(ExpandConstant('{commondesktop}\{#MyDesktopApkName}'));
    DeleteFile(ExpandConstant('{userdesktop}\{#MyDesktopApkName}'));
  end;
end;
