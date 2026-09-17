@echo off
setlocal enabledelayedexpansion
title IsoForge Wiederherstellung

rem ===========================================================================
rem  IsoForge - Wiederherstellung eines gesicherten Windows-Systems.
rem  Laeuft in der Windows-Wiederherstellungsumgebung (WinPE). Dort gibt es kein
rem  PowerShell, deshalb ist alles in Batch, diskpart, DISM und bcdboot gehalten.
rem ===========================================================================

set "WORK=X:\IsoForge\work"
if not exist "%WORK%" md "%WORK%" >nul 2>&1

rem --------------------------------------------------------------- Medium suchen
set "MEDIA="
for %%D in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
    if not defined MEDIA if exist "%%D:\IsoForge\media.cmd" set "MEDIA=%%D:"
)

if not defined MEDIA (
    echo.
    echo   Das IsoForge-Medium wurde nicht gefunden.
    echo   Pruefen Sie, ob das ISO noch eingelegt bzw. der USB-Datentraeger angeschlossen ist.
    echo.
    pause
    exit /b 1
)

call "%MEDIA%\IsoForge\media.cmd"
set "SOURCES=%MEDIA%\sources"

rem ------------------------------------------------------------ Firmware ermitteln
set "FIRMWARE=BIOS"
reg query HKLM\System\CurrentControlSet\Control /v PEFirmwareType 2>nul | find "0x2" >nul && set "FIRMWARE=UEFI"

rem ===========================================================================
:menu
cls
echo ===========================================================================
echo   IsoForge - Wiederherstellung
echo ===========================================================================
echo.
echo   Abbild        : %IF_TIERNAME%
echo   Erstellt am   : %IF_CREATED%
echo   Quellsystem   : %IF_SOURCE%
echo   Build-Kennung : %IF_BUILDID%
echo   Medium        : %MEDIA%
echo   Firmware      : %FIRMWARE%
echo.
echo ---------------------------------------------------------------------------
if "%IF_BOOTABLE%"=="1" echo   [1] Vollstaendige Wiederherstellung   - Zieldatentraeger wird geleert
if "%IF_BOOTABLE%"=="1" echo   [2] Parallel-Installation            - neue Partition, Auswahl beim Start
echo   [3] Dateien in einen Ordner entpacken - bestehendes System bleibt
echo   [4] Datentraeger anzeigen
echo   [5] Eingabeaufforderung
echo   [6] Neu starten
echo   [7] Herunterfahren
echo.
set "CHOICE="
set /p "CHOICE=  Auswahl: "

if "%CHOICE%"=="1" if "%IF_BOOTABLE%"=="1" goto full_restore
if "%CHOICE%"=="2" if "%IF_BOOTABLE%"=="1" goto parallel_restore
if "%CHOICE%"=="3" goto data_restore
if "%CHOICE%"=="4" goto show_disks
if "%CHOICE%"=="5" goto shell
if "%CHOICE%"=="6" wpeutil reboot
if "%CHOICE%"=="7" wpeutil shutdown
goto menu

rem ===========================================================================
:show_disks
cls
echo   Angeschlossene Datentraeger
echo   ---------------------------------------------------------------------------
> "%WORK%\list.txt" echo list disk
>>"%WORK%\list.txt" echo list volume
>>"%WORK%\list.txt" echo exit
diskpart /s "%WORK%\list.txt"
echo.
pause
goto menu

rem ===========================================================================
:shell
echo.
echo   Mit "exit" geht es zurueck zum Menue.
cmd.exe /k "cd /d X:\IsoForge"
goto menu

rem ===========================================================================
rem  Vollstaendige Wiederherstellung
rem ===========================================================================
:full_restore
cls
echo   Vollstaendige Wiederherstellung
echo   ---------------------------------------------------------------------------
> "%WORK%\list.txt" echo list disk
>>"%WORK%\list.txt" echo exit
diskpart /s "%WORK%\list.txt"
echo.
set "DISK="
set /p "DISK=  Nummer des Zieldatentraegers (leer = abbrechen): "
if not defined DISK goto menu

rem --- Bei BIOS/MBR ohne bootsect.exe laesst sich kein neuer MBR-Startcode schreiben.
set "WIPE=1"
if /i "%FIRMWARE%"=="BIOS" if not "%IF_HASBOOTSECT%"=="1" call :ask_mbr_strategy
if "%WIPE%"=="ABORT" goto menu

if "%WIPE%"=="1" (
    echo.
    echo   ACHTUNG: Auf Datentraeger %DISK% werden ALLE Partitionen und ALLE Daten geloescht.
    echo.
    set "CONFIRM="
    set /p "CONFIRM=  Zum Fortfahren LOESCHEN eintippen: "
    if /i not "!CONFIRM!"=="LOESCHEN" (
        echo   Abgebrochen.
        pause
        goto menu
    )
)

rem --- Groesse erfragen, wenn zusaetzliche Datentraeger im Abbild liegen ------
set "WINSIZE="
if defined IF_EXTRA (
    echo.
    echo   Das Abbild enthaelt zusaetzlich die Datentraeger: %IF_EXTRA%
    set /p "WINSIZE=  Groesse der Windows-Partition in GB (leer = restlicher Platz): "
)

if "%WIPE%"=="1" (
    call :layout_clean %DISK%
) else (
    call :layout_reuse %DISK%
)

if not exist "W:\" (
    echo.
    echo   Die Windows-Partition wurde nicht eingerichtet - Abbruch.
    pause
    goto menu
)

call :apply_image "W:\"
if errorlevel 1 (
    echo   Das Abbild konnte nicht angewendet werden.
    pause
    goto menu
)

call :restore_extras %DISK%
call :universal_restore W:
call :inject_drivers W:
call :stamp W:
call :write_boot W: %SYSDRIVE% %FIRMWARE% %WIPE%

echo.
echo   ===========================================================================
echo   Die Wiederherstellung ist abgeschlossen.
echo   ===========================================================================
echo.
set "RB="
set /p "RB=  Jetzt neu starten? (j/n): "
if /i "%RB%"=="j" wpeutil reboot
goto menu

rem ===========================================================================
rem  Parallel-Installation: zusaetzliche Partition, bestehendes System bleibt
rem ===========================================================================
:parallel_restore
cls
echo   Parallel-Installation
echo   ---------------------------------------------------------------------------
echo   Es wird eine neue Partition im nicht zugewiesenen Bereich angelegt. Vorhandene
echo   Partitionen und das bereits installierte Windows bleiben unveraendert; beim
echo   Start erscheint kuenftig eine Auswahl.
echo.
> "%WORK%\list.txt" echo list disk
>>"%WORK%\list.txt" echo exit
diskpart /s "%WORK%\list.txt"
echo.
set "DISK="
set /p "DISK=  Nummer des Datentraegers mit freiem Speicher (leer = abbrechen): "
if not defined DISK goto menu

set "PSIZE="
set /p "PSIZE=  Groesse der neuen Partition in GB (leer = gesamter freier Platz): "

> "%WORK%\part.txt" echo select disk %DISK%
if defined PSIZE (
    set /a PSIZEMB=%PSIZE%*1024
    >>"%WORK%\part.txt" echo create partition primary size=!PSIZEMB!
) else (
    >>"%WORK%\part.txt" echo create partition primary
)
>>"%WORK%\part.txt" echo format quick fs=ntfs label="IsoForge %IF_BUILDID%"
>>"%WORK%\part.txt" echo assign letter=W
>>"%WORK%\part.txt" echo exit

echo.
echo   Partition wird angelegt ...
diskpart /s "%WORK%\part.txt"

if not exist "W:\" (
    echo.
    echo   Die neue Partition liess sich nicht anlegen. Ist genug nicht zugewiesener
    echo   Speicher vorhanden? Verkleinern Sie dafuer vorher eine bestehende Partition.
    pause
    goto menu
)

call :find_system_partition %DISK%
if not defined SYSDRIVE (
    echo.
    echo   Die vorhandene Systempartition wurde nicht gefunden. Ohne sie laesst sich
    echo   kein zusaetzlicher Starteintrag anlegen.
    pause
    goto menu
)
echo   Vorhandene Systempartition: %SYSDRIVE%

call :apply_image "W:\"
if errorlevel 1 (
    echo   Das Abbild konnte nicht angewendet werden.
    pause
    goto menu
)

call :universal_restore W:
call :inject_drivers W:
call :stamp W:
call :write_boot W: %SYSDRIVE% %FIRMWARE% 0

echo.
echo   ===========================================================================
echo   Die Parallel-Installation ist eingerichtet.
echo   Beim naechsten Start erscheint zusaetzlich der Eintrag:
echo     Windows - IsoForge %IF_BUILDID% (%IF_CREATED%)
echo   ===========================================================================
echo.
set "RB="
set /p "RB=  Jetzt neu starten? (j/n): "
if /i "%RB%"=="j" wpeutil reboot
goto menu

rem ===========================================================================
rem  Dateien in einen Ordner entpacken
rem ===========================================================================
:data_restore
cls
echo   Dateien in einen Ordner entpacken
echo   ---------------------------------------------------------------------------
> "%WORK%\list.txt" echo list volume
>>"%WORK%\list.txt" echo exit
diskpart /s "%WORK%\list.txt"
echo.
set "TARGET="
set /p "TARGET=  Ziel-Laufwerksbuchstabe (z. B. D, leer = abbrechen): "
if not defined TARGET goto menu

set "DEST=%TARGET%:\IsoForge-%IF_BUILDID%"
echo.
echo   Ziel: %DEST%
if not exist "%DEST%" md "%DEST%"

call :apply_image "%DEST%\"
if errorlevel 1 (
    echo   Das Abbild konnte nicht entpackt werden.
) else (
    echo.
    echo   Die Dateien liegen jetzt unter %DEST%.
)
echo.
pause
goto menu

rem ===========================================================================
rem  Unterprogramme
rem ===========================================================================

rem --- Vorgehen bei MBR ohne bootsect.exe erfragen ---------------------------
:ask_mbr_strategy
echo.
echo   Hinweis: Dieser Rechner startet im BIOS-Modus (MBR) und das Medium enthaelt
echo   kein bootsect.exe. Wird der Datentraeger vollstaendig geleert, laesst sich der
echo   Startcode im Master Boot Record hier nicht neu schreiben - der Rechner wuerde
echo   nach der Wiederherstellung nicht starten.
echo.
echo   [1] Partitionierung beibehalten, Zielpartition nur neu formatieren (empfohlen)
echo   [2] Trotzdem vollstaendig leeren
echo   [3] Abbrechen
set "S="
set /p "S=  Auswahl: "
if "%S%"=="1" set "WIPE=0"
if "%S%"=="2" set "WIPE=1"
if "%S%"=="3" set "WIPE=ABORT"
exit /b 0

rem --- Datentraeger leeren und neu aufteilen ---------------------------------
rem  %1 = Datentraegernummer
:layout_clean
set "SYSDRIVE=S:"
> "%WORK%\part.txt" echo select disk %~1
>>"%WORK%\part.txt" echo clean
if /i "%FIRMWARE%"=="UEFI" (
    >>"%WORK%\part.txt" echo convert gpt
    >>"%WORK%\part.txt" echo create partition efi size=300
    >>"%WORK%\part.txt" echo format quick fs=fat32 label="System"
    >>"%WORK%\part.txt" echo assign letter=S
    >>"%WORK%\part.txt" echo create partition msr size=16
) else (
    >>"%WORK%\part.txt" echo convert mbr
    >>"%WORK%\part.txt" echo create partition primary size=500
    >>"%WORK%\part.txt" echo format quick fs=ntfs label="System"
    >>"%WORK%\part.txt" echo assign letter=S
    >>"%WORK%\part.txt" echo active
)
if defined WINSIZE (
    set /a WINSIZEMB=%WINSIZE%*1024
    >>"%WORK%\part.txt" echo create partition primary size=!WINSIZEMB!
) else (
    >>"%WORK%\part.txt" echo create partition primary
)
>>"%WORK%\part.txt" echo format quick fs=ntfs label="Windows"
>>"%WORK%\part.txt" echo assign letter=W
>>"%WORK%\part.txt" echo exit

echo.
echo   Partitionen werden angelegt ...
diskpart /s "%WORK%\part.txt"
exit /b 0

rem --- Vorhandene Aufteilung behalten, nur die Zielpartition formatieren -----
rem  %1 = Datentraegernummer
:layout_reuse
echo.
echo   Partitionen auf Datentraeger %~1:
> "%WORK%\part.txt" echo select disk %~1
>>"%WORK%\part.txt" echo list partition
>>"%WORK%\part.txt" echo exit
diskpart /s "%WORK%\part.txt"
echo.
set "WINPART="
set /p "WINPART=  Nummer der Partition fuer Windows: "
if not defined WINPART exit /b 1

> "%WORK%\part.txt" echo select disk %~1
>>"%WORK%\part.txt" echo select partition %WINPART%
>>"%WORK%\part.txt" echo format quick fs=ntfs label="Windows"
>>"%WORK%\part.txt" echo assign letter=W
>>"%WORK%\part.txt" echo exit

echo.
echo   Partition %WINPART% wird formatiert ...
diskpart /s "%WORK%\part.txt"

call :find_system_partition %~1
if not defined SYSDRIVE (
    echo   Die Systempartition wurde nicht gefunden; es wird W: verwendet.
    set "SYSDRIVE=W:"
)
exit /b 0

rem --- Zusaetzliche Datentraeger aus dem Abbild ------------------------------
:restore_extras
if not defined IF_EXTRA exit /b 0
for %%V in (%IF_EXTRA%) do (
    if exist "%SOURCES%\data_%%V.wim" (
        echo.
        echo   Zusaetzlicher Datentraeger %%V: liegt im Abbild.
        set "ESIZE="
        set /p "ESIZE=  Groesse in GB (leer = restlicher Platz, 0 = ueberspringen): "
        if not "!ESIZE!"=="0" (
            > "%WORK%\extra.txt" echo select disk %~1
            if defined ESIZE (
                set /a ESIZEMB=!ESIZE!*1024
                >>"%WORK%\extra.txt" echo create partition primary size=!ESIZEMB!
            ) else (
                >>"%WORK%\extra.txt" echo create partition primary
            )
            >>"%WORK%\extra.txt" echo format quick fs=ntfs label="Daten %%V"
            >>"%WORK%\extra.txt" echo assign letter=%%V
            >>"%WORK%\extra.txt" echo exit
            diskpart /s "%WORK%\extra.txt"
            echo   Daten werden geschrieben ...
            dism /Apply-Image /ImageFile:"%SOURCES%\data_%%V.wim" /Index:1 /ApplyDir:%%V:\
        )
    )
)
exit /b 0

rem --- Abbild anwenden -------------------------------------------------------
rem  %1 = Zielverzeichnis
:apply_image
echo.
echo   Das Abbild wird geschrieben. Je nach Datenmenge dauert das eine Weile.
echo.
if "%IF_SPLIT%"=="1" (
    dism /Apply-Image /ImageFile:"%SOURCES%\%IF_IMAGE%" /SWMFile:"%SOURCES%\%IF_IMAGESTEM%*.swm" /Index:1 /ApplyDir:"%~1"
) else (
    dism /Apply-Image /ImageFile:"%SOURCES%\%IF_IMAGE%" /Index:1 /ApplyDir:"%~1"
)
exit /b %errorlevel%

rem --- Universelle Wiederherstellung ----------------------------------------
rem  Schaltet die Speichertreiber frei, die Windows zum Starten braucht. Ohne das
rem  bleibt ein Abbild von echter Hardware in einer VM - und umgekehrt - mit
rem  INACCESSIBLE_BOOT_DEVICE stehen.
rem  %1 = Laufwerk des wiederhergestellten Windows
:universal_restore
set "WD=%~1"
if not exist "%WD%\Windows\System32\config\SYSTEM" exit /b 0

echo.
echo   Speichertreiber fuer fremde Hardware werden freigeschaltet ...

reg load HKLM\IFSYS "%WD%\Windows\System32\config\SYSTEM" >nul 2>&1
if errorlevel 1 (
    echo   Die Registrierung des Abbilds liess sich nicht laden - Schritt wird uebersprungen.
    exit /b 0
)

set "CS=ControlSet001"
for /f "tokens=3" %%a in ('reg query HKLM\IFSYS\Select /v Current 2^>nul ^| find /i "Current"') do (
    set /a CSNUM=%%a 2>nul
    if !CSNUM! GEQ 1 if !CSNUM! LEQ 9 set "CS=ControlSet00!CSNUM!"
)

for %%S in (
    storahci stornvme storufs msahci atapi intelide pciide aliide amdide cmdide viaide
    iaStorV iaStorAV iaStorAC iaStorE amdsata amdxata nvraid nvstor
    LSI_SAS LSI_SAS2 LSI_SAS3 LSI_SCSI LSI_FC megasas megasas35 megasr percsas2i percsas3i
    arcsas adp94xx adpahci adpu320 elxstor HpSAMD SiSRaid2 SiSRaid4 vsmraid vhdmp
    3ware aic78xx iteraid iteatapi ulsata ulsata2 uliahci
    vioscsi viostor vioinput balloon netkvm
    vmbus storvsc VMBusHID storflt hvservice
    VMSCSI vmci vmusbmouse pvscsi vsock
    xenvbd xenbus
) do (
    reg query "HKLM\IFSYS\!CS!\Services\%%S" >nul 2>&1 && reg add "HKLM\IFSYS\!CS!\Services\%%S" /v Start /t REG_DWORD /d 0 /f >nul 2>&1
)

reg unload HKLM\IFSYS >nul 2>&1
echo   Erledigt (Konfigurationssatz !CS!).
exit /b 0

rem --- Treiber vom Medium einspielen ----------------------------------------
:inject_drivers
if not exist "%MEDIA%\drivers" exit /b 0
echo.
echo   Zusaetzliche Treiber vom Medium werden eingespielt ...
dism /Image:%~1\ /Add-Driver /Driver:"%MEDIA%\drivers" /Recurse /ForceUnsigned
exit /b 0

rem --- Herkunft im wiederhergestellten System vermerken ---------------------
:stamp
set "WD=%~1"
if not exist "%WD%\IsoForge" md "%WD%\IsoForge" >nul 2>&1
> "%WD%\IsoForge\restored.txt" echo IsoForge-Build  : %IF_BUILDID% %IF_BUILDGUID%
>>"%WD%\IsoForge\restored.txt" echo Abbild          : %IF_TIERNAME%
>>"%WD%\IsoForge\restored.txt" echo Gesichert am    : %IF_CREATED%
>>"%WD%\IsoForge\restored.txt" echo Quellsystem     : %IF_SOURCE%
>>"%WD%\IsoForge\restored.txt" echo Zurueckgespielt : %DATE% %TIME%
exit /b 0

rem --- Systempartition eines vorhandenen Starts suchen ----------------------
rem  %1 = Datentraegernummer
:find_system_partition
set "SYSDRIVE="
set "SYSPART="

if /i "%FIRMWARE%"=="UEFI" (
    > "%WORK%\fp.txt" echo select disk %~1
    >>"%WORK%\fp.txt" echo list partition
    >>"%WORK%\fp.txt" echo exit
    diskpart /s "%WORK%\fp.txt" > "%WORK%\fp.out"

    for /f "tokens=2" %%p in ('findstr /i /c:"System" "%WORK%\fp.out"') do (
        if not defined SYSPART set "SYSPART=%%p"
    )

    if defined SYSPART (
        > "%WORK%\fp2.txt" echo select disk %~1
        >>"%WORK%\fp2.txt" echo select partition !SYSPART!
        >>"%WORK%\fp2.txt" echo assign letter=S
        >>"%WORK%\fp2.txt" echo exit
        diskpart /s "%WORK%\fp2.txt" >nul 2>&1
        if exist "S:\" set "SYSDRIVE=S:"
    )
) else (
    rem MBR: die aktive Partition traegt den Startmanager.
    for %%D in (C D E F G H I J K L M N O P Q R S T U V Y Z) do (
        if not defined SYSDRIVE if exist "%%D:\bootmgr" set "SYSDRIVE=%%D:"
    )
)
exit /b 0

rem --- Startdateien schreiben ------------------------------------------------
rem  %1 = Windows-Laufwerk, %2 = Systempartition, %3 = Firmware, %4 = 1 bei geleertem Datentraeger
:write_boot
set "WD=%~1"
set "SD=%~2"
set "FW=%~3"
set "WIPED=%~4"

if not defined SD set "SD=%WD%"

echo.
echo   Startdateien werden nach %SD% geschrieben ...

if /i "%FW%"=="UEFI" (
    bcdboot %WD%\Windows /s %SD% /f UEFI
) else (
    bcdboot %WD%\Windows /s %SD% /f BIOS
    if "%WIPED%"=="1" if "%IF_HASBOOTSECT%"=="1" (
        echo   Startcode im Master Boot Record wird geschrieben ...
        "%MEDIA%\IsoForge\tools\bootsect.exe" /nt60 %SD% /mbr /force
    )
)

if errorlevel 1 (
    echo   bcdboot hat einen Fehler gemeldet. Der Starteintrag ist moeglicherweise unvollstaendig.
    exit /b 1
)

rem --- Eintrag benennen, damit mehrere Staende unterscheidbar bleiben --------
set "STORE="
if /i "%FW%"=="UEFI" (
    if exist "%SD%\EFI\Microsoft\Boot\BCD" set "STORE=%SD%\EFI\Microsoft\Boot\BCD"
) else (
    if exist "%SD%\Boot\BCD" set "STORE=%SD%\Boot\BCD"
)

if defined STORE (
    bcdedit /store "!STORE!" /set {default} description "Windows - IsoForge %IF_BUILDID% (%IF_CREATED%)" >nul 2>&1
    bcdedit /store "!STORE!" /timeout 10 >nul 2>&1
)

echo   Fertig.
exit /b 0
