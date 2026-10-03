# Документация функциональных модулей «Ремонт и очистка Windows» (`WinRepair`)

Все модули очистки (`ICleanupModule`) и восстановления (`IRepairModule`) реализуют обязательный трёхэтапный контракт:
`ScanAsync()` → `PreviewAsync()` → `ExecuteAsync()`.

Перед первой изменяющей операцией в сессии сервис `SessionSafetyGuard` автоматически создаёт контрольную точку восстановления Windows. Если точку восстановления создать не удалось — выполнение изменяющей операции блокируется.

---

## 1. Модули диагностики (`DiagnosticService` — только чтение)

| Подсистема | Что проверяет | Используемые команды и API | Уровень риска | Откат |
|---|---|---|---|---|
| **Диски и SMART** | Свободное место, тип накопителя (NVMe/SSD/HDD), статус SMART, температура, грязный бит NTFS | `DriveInfo`, WMI `root\Microsoft\Windows\Storage:MSFT_PhysicalDisk`, `root\wmi:MSStorageDriver_FailurePredictStatus`, `fsutil dirty query` | Безопасно (только чтение) | Не требуется |
| **Целостность системных файлов** | Состояние хранилища компонентов `WinSxS` и целостность образа ОС | `dism.exe /Online /Cleanup-Image /CheckHealth` и `/ScanHealth`, парсер `SystemOutputParser` | Безопасно (только чтение) | Не требуется |
| **Журнал событий Windows** | Критические сбои и ошибки в журнале `System` за последние 30 дней | `System.Diagnostics.EventLog("System")` | Безопасно (только чтение) | Не требуется |
| **Системные службы** | Остановленные критичные службы с типом запуска «Автоматически» (`wuauserv`, `BITS`, `CryptSvc`, `MpsSvc`, `WinDefend` и др.) | `System.ServiceProcess.ServiceController` | Безопасно (только чтение) | Не требуется |
| **Драйверы и устройства** | Устройства с ненулевым кодом ошибки в Диспетчере устройств | WMI `SELECT Name, DeviceID, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0` | Безопасно (только чтение) | Не требуется |
| **Сеть и DNS** | Активные сетевые адаптеры, задержка резолвинга DNS, системный прокси, состояние Winsock | `NetworkInterface.GetAllNetworkInterfaces()`, `Dns.GetHostEntryAsync()`, реестр `HKCU\...\Internet Settings` | Безопасно (только чтение) | Не требуется |
| **Центр обновления Windows** | Статус служб обновления, список установленных пакетов `KB` и дата последнего обновления | WMI `Win32_QuickFixEngineering`, `ServiceController` | Безопасно (только чтение) | Не требуется |
| **Загрузка и автозапуск** | Режим прошивки (`UEFI` / `Legacy BIOS`), время работы, число и влияние элементов автозагрузки | Win32 `GetFirmwareEnvironmentVariableW` (`kernel32.dll`), ветки `Run` / `RunOnce` | Безопасно (только чтение) | Не требуется |
| **Электропитание и батарея** | Активная схема питания, заряд и износ аккумулятора (для ноутбуков) | `powercfg.exe /getactivescheme`, WMI `Win32_Battery` | Безопасно (только чтение) | Не требуется |
| **Безопасность и защита** | Состояние Защитника Windows, Брандмауэра и наличие точек восстановления | Реестр `FirewallPolicy`, WMI `root\default:SystemRestore` | Безопасно (только чтение) | Не требуется |

---

## 2. Модули очистки (`ICleanupModule` — с обязательным предпросмотром)

| ID модуля | Класс | Что делает | Вызываемые API / пути | Уровень риска | Механизм отката |
|---|---|---|---|---|---|
| `clean.system_junk` | `SystemJunkCleanupModule` | Очистка временных файлов системы и профилей | `C:\Windows\Temp`, `C:\Windows\Prefetch\*.pf`, `%LOCALAPPDATA%\Temp` + `PathValidator` | `Safe` (Безопасно) | Возврат файлов из пакета карантина `%ProgramData%\WinRepair\Quarantine\<дата>\` |
| `clean.recycle_bin` | `RecycleBinCleanupModule` | Очистка Корзины на всех локальных томах | `<Drive>:\$Recycle.Bin`, Win32 `SHQueryRecycleBinW` / `SHEmptyRecycleBinW` (`shell32.dll`) | `Low` (Низкий) | Возврат объектов из карантина |
| `clean.wu_cache` | `WindowsUpdateCacheCleanupModule` | Удаление скачанных пакетов установок обновлений | `C:\Windows\SoftwareDistribution\Download` | `Safe` (Безопасно) | Возврат файлов из карантина |
| `clean.browser_cache` | `BrowserCacheCleanupModule` | Очистка кэша браузеров (без затрагивания паролей, куки, истории и закладок) | Папки `Cache`, `Code Cache`, `GPUCache`, `cache2`, `startupCache` для Edge, Chrome, Firefox, Яндекс.Браузера, Opera | `Safe` (Безопасно) | Возврат файлов из карантина |
| `clean.thumbnails` | `ThumbnailsAndIconsCleanupModule` | Очистка баз эскизов и кэша иконок Проводника | `%LOCALAPPDATA%\Microsoft\Windows\Explorer\thumbcache_*.db`, `iconcache_*.db` | `Safe` (Безопасно) | Возврат файлов из карантина |
| `clean.dumps_logs` | `DumpsAndLogsCleanupModule` | Очистка аварийных дампов памяти и логов сбоев | `C:\Windows\Minidump\*.dmp`, `C:\Windows\LiveKernelReports\*.dmp`, `%LOCALAPPDATA%\CrashDumps\*.dmp` | `Safe` (Безопасно) | Возврат файлов из карантина |
| `clean.shader_cache` | `DirectXShaderCacheCleanupModule` | Очистка кэша DirectX и шейдеров видеодрайверов | `%LOCALAPPDATA%\D3DSCache`, `NVIDIA\DXCache`, `NVIDIA\GLCache`, `AMD\DxCache`, `Intel\ShaderCache` | `Safe` (Безопасно) | Возврат файлов из карантина |
| `clean.program_remnants` | `OrphanedProgramRemnantsCleanupModule` | Удаление пустых папок удалённых программ и недействительных ключей `Uninstall` | `IProgramManagerService`, `reg.exe export` (бэкап перед удалением ключа) | `Low` (Низкий) | Импорт `.reg`-копии реестра и возврат из карантина |
| `clean.wer_cbs` | `WindowsErrorReportingCleanupModule` | Очистка отчётов об ошибках Windows и старых ротированных логов CBS/DISM | `ProgramData\Microsoft\Windows\WER`, `%LOCALAPPDATA%\Microsoft\Windows\WER`, `CBSPersist_*.log` | `Safe` (Безопасно) | Возврат файлов из карантина |
| `clean.dism_components` | `OldWindowsUpdatesDismCleanupModule` | Штатная очистка устаревших версий пакетов обновлений в `WinSxS` | `dism.exe /Online /Cleanup-Image /AnalyzeComponentStore` и `/StartComponentCleanup` | `Low` (Низкий) | Откат к созданной точке восстановления Windows |

> **Поиск больших и дублирующихся файлов (`LargeAndDuplicateFileScanner`):** выполняет группировку по размеру и расчёт криптографического хеша `SHA-256`. Работает строго в режиме показа списка — автоматическое удаление запрещено.

---

## 3. Модули восстановления (`IRepairModule`)

| ID модуля | Класс | Что делает | Вызываемые команды и API | Уровень риска и подтверждение | Механизм отката |
|---|---|---|---|---|---|
| `repair.sfc_scannow` | `SystemFilesSfcRepairModule` | Проверка и восстановление защищённых системных файлов Windows | `sfc.exe /verifyonly` (Scan), `sfc.exe /scannow` (Execute) + `SystemOutputParser` | `Medium` (Средний) | Откат к созданной точке восстановления системы |
| `repair.dism_restore` | `ComponentStoreDismRepairModule` | Восстановление хранилища компонентов `WinSxS` (с автопоиском локального `install.wim`) | `dism.exe /Online /Cleanup-Image /RestoreHealth` (или `/Source:WIM:"...\install.wim":1 /LimitAccess`) | `Medium` (Средний) | Откат к созданной точке восстановления системы |
| `repair.network_stack` | `NetworkStackRepairModule` | Сброс Winsock, TCP/IP, кэша DNS и отключение системного прокси | `ipconfig /flushdns`, `netsh winsock reset`, `netsh int ip reset`, `netsh winhttp reset proxy`, сброс `ProxyEnable` | `High` — ввод фразы **`СБРОСИТЬ СЕТЬ`** | Импорт автоматически сохранённого `.reg`-файла `Internet Settings` + точка восстановления |
| `repair.windows_update` | `WindowsUpdateRepairModule` | Перезапуск и перерегистрация служб Центра обновления и сброс папок кэша | `net stop/start` и `sc config` для `bits`, `wuauserv`, `cryptsvc`, `msiserver`; переименование `SoftwareDistribution` и `catroot2` в `.bak` | `Medium` (Средний) | Обратное переименование папок `.bak` и возврат конфигурации служб |
| `repair.file_associations` | `FileAssociationsRepairModule` | Восстановление стандартных ассоциаций системных классов (`.exe`, `.lnk`, `.bat`, `.cmd`, `.reg`) | `reg.exe export HKLM\SOFTWARE\Classes\.exe`, `assoc .exe=exefile`, `assoc .lnk=lnkfile` и др. | `Medium` (Средний) | Импорт сохранённого `.reg`-файла классов из журнала операций |
| `repair.power_schemes` | `PowerSchemesRepairModule` | Восстановление стандартных схем электропитания Windows | `powercfg.exe -restoredefaultschemes` | `Low` (Низкий) | Переключение схемы через `powercfg /setactive` |
| `repair.chkdsk` | `DiskCheckChkdskRepairModule` | Онлайн-проверка файловой системы тома `C:` и планирование полной проверки при перезагрузке | `fsutil dirty query C:`, `chkdsk.exe C: /scan`, `chkntfs.exe /c C:` | `High` — ввод фразы **`ПРОВЕРИТЬ ДИСК`** | Отмена запланированной проверки командой `chkntfs.exe /x C:` из журнала операций |
| `repair.bootloader` | `BootloaderRepairModule` | Резервное копирование `BCD` и восстановление загрузочных записей UEFI/MBR | `bcdedit.exe /export`, `bcdboot.exe C:\Windows /l ru-ru`, а в среде WinRE/Legacy: `bootrec.exe /fixmbr`, `/fixboot`, `/rebuildbcd` | `High` — ввод фразы **`ВОССТАНОВИТЬ ЗАГРУЗЧИК`** | Импорт сохранённой резервной копии хранилища командой `bcdedit.exe /import "<путь>"` |
| `repair.restore_points` | `SystemRestorePointsRepairModule` | Включение защиты системы на диске `C:` и создание точки восстановления | PowerShell `Enable-ComputerRestore`, WMI `root\default:SystemRestore.CreateRestorePoint` | `Safe` (Безопасно) | Запуск системного `rstrui.exe` |

---

## 4. Оптимизация, управление программами и служебные сервисы

| Сервис | Что делает | Вызываемые API и команды | Уровень риска | Механизм отката |
|---|---|---|---|---|
| `OptimizationService` (Автозагрузка) | Чтение и включение/отключение элементов из `Run`, `RunOnce` и папок «Автозагрузка» | `HKCU`/`HKLM` `...\Explorer\StartupApproved\Run` + предварительный `.reg` экспорт | `Low` (Низкий) | Повторное включение в UI или импорт `.reg`-бэкапа |
| `OptimizationService` (Службы) | Перевод некритичных фоновых служб в рекомендуемый режим запуска | `sc.exe config <Service> start= <mode>`, экспорт `HKLM\SYSTEM\CurrentControlSet\Services\<Service>` | `Low` (Низкий) | Автоматическая команда возврата исходного режима `sc.exe config` и импорт `.reg` |
| `OptimizationService` (Задачи) | Просмотр и отключение некритичных задач Планировщика | `schtasks.exe /Query /FO CSV`, `schtasks.exe /Change /TN "..." /DISABLE` | `Low` (Низкий) | Обратная команда `schtasks.exe /Change /TN "..." /ENABLE` |
| `OptimizationService` (Память) | Честная выгрузка неактивных страниц из рабочего набора процессов без «ускорителей»-обманок | Win32 P/Invoke `EmptyWorkingSet` (`psapi.dll`) | `Safe` (Безопасно) | Менеджер памяти Windows автоматически подгружает страницы по мере обращения |
| `ProgramManagerService` | Список установленного ПО, запуск деинсталлятора, поиск остатков удалённых программ | Ветки `Uninstall` (64-бит и `WOW6432Node`), проверка пустых папок `Program Files` и осиротевших ключей с `.reg`-бэкапом | `Low` (Низкий) | Импорт `.reg`-копии удалённого ключа из журнала операций |
| `QuarantineService` | Помещение удаляемых файлов в `%ProgramData%\WinRepair\Quarantine\<дата>\` с манифестом и `SHA-256` | `File.Move`, `SHA256.HashDataAsync`, `manifest.json` | `Safe` (Безопасно) | Поштучное или пакетное восстановление по исходным путям |
| `OperationJournalService` | Полный аудит всех операций и откат по каждому пункту | `%ProgramData%\WinRepair\Journal\operations.json`, `Serilog` (`%ProgramData%\WinRepair\Logs`) | `Safe` (Безопасно) | Вызов `RollbackOperationAsync(operationId)` |
