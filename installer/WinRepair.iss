; ============================================================================
; Скрипт установщика Inno Setup 6 для приложения «Ремонт и очистка Windows»
; Поддержка: Windows 10 (1809+, сборка 17763+) и Windows 11 (21H2+, x64)
; Язык интерфейса мастера установки: Русский (по умолчанию)
; Поддержка ключей тихой установки: /SILENT, /VERYSILENT, /DIR="..."
; ============================================================================

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif

#ifndef OutputDistDir
  #define OutputDistDir "..\dist"
#endif

#define MyAppName "Ремонт и очистка Windows"
#define MyAppInternalName "WinRepair"
#define MyAppPublisher "WinRepair Engineering"
#define MyAppURL "https://localhost/winrepair"
#define MyAppExeName "WinRepair.App.exe"
#define MyAppId "{{8E4A9D21-7C3F-4B19-9E52-3D6A1F8B9042}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Установщик утилиты «{#MyAppName}»
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

; Папка установки по умолчанию (64-разрядный режим Program Files)
DefaultDirName={autopf}\{#MyAppInternalName}
DefaultGroupName={#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=yes
UsePreviousAppDir=yes

; Лицензионное соглашение на русском языке
LicenseFile=LICENSE_RU.txt

; Требование прав администратора и минимальной версии ОС Windows 10 1809 (10.0.17763)
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Корректное обновление поверх установленной версии
CloseApplications=force
CloseApplicationsFilter=*.exe
RestartApplications=no

; Выходной файл установщика
OutputDir={#OutputDistDir}
OutputBaseFilename=WinRepair-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

; Регистрация в «Установка и удаление программ»
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
CreateUninstallRegKey=yes

; Заготовка под цифровую подпись кода (активируется при передаче /DSignToolConfigured=1 в ISCC.exe)
#ifdef SignToolConfigured
SignTool=signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $f
SignedUninstaller=yes
#endif

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Messages]
russian.BeveledLabel=Ремонт и очистка Windows — версия {#MyAppVersion}
russian.WelcomeLabel2=Мастер установит приложение «[name/ver]» на ваш компьютер.%n%nПрограмма предназначена для комплексной диагностики, безопасной очистки с предпросмотром и карантином, а также восстановления системных компонентов Windows 10 и Windows 11.%n%nПеред продолжением рекомендуется закрыть все прочие программы.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Dirs]
; Каталоги данных пользователя (НЕ удаляются при деинсталляции — п. 6.3 и п. 8 ТЗ)
Name: "{commonappdata}\WinRepair"; Flags: uninsneveruninstall
Name: "{commonappdata}\WinRepair\Logs"; Flags: uninsneveruninstall
Name: "{commonappdata}\WinRepair\Quarantine"; Flags: uninsneveruninstall
Name: "{commonappdata}\WinRepair\Journal"; Flags: uninsneveruninstall
Name: "{commonappdata}\WinRepair\Backups"; Flags: uninsneveruninstall
Name: "{commonappdata}\WinRepair\Config"; Flags: uninsneveruninstall

[Files]
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "LICENSE_RU.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Comment: "Диагностика, очистка и восстановление Windows"
Name: "{group}\Удалить {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon; Comment: "Диагностика, очистка и восстановление Windows"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
; При удалении приложения снимаем задачу автоматической очистки из Планировщика заданий,
; но сохраняем карантин и журнал операций пользователя в %ProgramData%\WinRepair
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""\WinRepair\Автоматическая очистка системы"" /F"; Flags: runhidden; RunOnceId: "RemoveWinRepairScheduledTask"

[Code]
function InitializeSetup(): Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);

  if not IsAdminInstallMode then
  begin
    MsgBox('Для установки программы «Ремонт и очистка Windows» необходимы права администратора. Пожалуйста, запустите установщик от имени администратора.', mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

  if (Version.Major < 10) or ((Version.Major = 10) and (Version.Build < 17763)) then
  begin
    MsgBox('Программа «Ремонт и очистка Windows» требует операционную систему Windows 10 (версия 1809, сборка 17763 и новее) или Windows 11.', mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if not UninstallSilent then
    begin
      MsgBox('Программа «Ремонт и очистка Windows» была удалена с компьютера.' + #13#10 + #13#10 +
             'В целях безопасности ваш журнал операций, резервные копии реестра и файлы карантина сохранены в каталоге:' + #13#10 +
             ExpandConstant('{commonappdata}\WinRepair'),
             mbInformation, MB_OK);
    end;
  end;
end;
