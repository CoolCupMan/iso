@echo off
rem IsoForge - Startpunkt der Wiederherstellungsumgebung.
rem winpeshl.ini ruft diese Datei anstelle der Windows-Wiederherstellungsoberflaeche auf.

title IsoForge

rem Netzwerk, Geraete und Umgebungsvariablen einrichten. Ohne wpeinit fehlen u. a. Laufwerksbuchstaben.
wpeinit

rem Bildschirmaufloesung anheben, damit die Ausgabe lesbar bleibt.
wpeutil UpdateBootInfo >nul 2>&1

cd /d X:\IsoForge
call X:\IsoForge\Restore.cmd

rem Faellt das Skript durch, bleibt wenigstens eine Eingabeaufforderung stehen.
echo.
echo Das Wiederherstellungsskript wurde beendet. Diese Eingabeaufforderung bleibt offen.
cmd.exe
