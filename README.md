# IsoForge

Sichert ein **laufendes Windows** in ein **startfähiges ISO-Abbild**. Das Abbild lässt sich
anschließend in einer virtuellen Maschine, auf einer Partition oder auf einer kompletten Festplatte
wiederherstellen – mit allen Programmen, Einstellungen und Dateien, so wie das System zum Zeitpunkt
der Sicherung aussah.

Der Rechner darf dabei normal weiterlaufen. Grundlage ist der **Volume Shadow Copy Service** von
Windows: Er friert den Zustand des Laufwerks zu einem Zeitpunkt ein, sodass auch geöffnete Dateien –
Registrierung, Datenbanken, Postfächer – in sich stimmig gesichert werden.

---

## Auf einen Blick

| | |
|---|---|
| Läuft auf | Windows 10 / 11 / Server (x64), als Administrator |
| Voraussetzungen | keine – kein Windows-ADK, kein .NET, keine weiteren Werkzeuge |
| Ergebnis | eine `.iso`-Datei, die im BIOS- **und** im UEFI-Modus startet |
| Zurückspielen auf | virtuelle Maschine, einzelne Partition, ganze Festplatte |
| Abstufungen | alles (1:1) · nur System und Programme · nur persönliche Dateien · eigene Auswahl |
| Parallelbetrieb | ja – jeder Stand bekommt eine eigene Build-Kennung und einen eigenen Starteintrag |

---

## Die Abstufungen

| Abstufung | Was hinein kommt | Was fehlt | Startfähig |
|---|---|---|---|
| `full` | Windows, alle Programme, alle Einstellungen, alle Benutzerdateien | Auslagerungsdatei, Ruhezustandsdatei, Papierkorb, temporäre Dateien | ja |
| `system` | Windows, Programme, Programmeinstellungen (inklusive `AppData`) | Dokumente, Desktop, Downloads, Bilder, Musik, Videos, Favoriten, OneDrive | ja |
| `personal` | ausschließlich die Benutzerprofile mit den persönlichen Dateien | Windows, Programme, `ProgramData` | nein¹ |
| `custom` | wie `full`, zusätzlich um eine eigene Ausschlussliste ergänzt | was in der Liste steht | ja |

¹ Das Medium startet trotzdem – es bietet dann aber nur das Entpacken der Dateien in einen Ordner an,
keine Systemwiederherstellung.

---

## Verwendung

### Assistent

Ohne Argumente startet ein Assistent, der durch Abstufung, Ziel und Verdichtung führt:

```
IsoForge-1.0.0-7K3QD9WZ.exe
```

### Kommandozeile

```bat
:: Vollständige 1:1-Sicherung auf ein externes Laufwerk
IsoForge.exe capture --tier full --output E:\Backups

:: Ohne persönliche Dateien, dafür schneller
IsoForge.exe capture --tier system --compression fast --output E:\Backups

:: Nur die Benutzerdateien, zusätzlich das Datenlaufwerk D:
IsoForge.exe capture --tier personal --extra-volumes D --output E:\Backups

:: Platzbedarf vorher abschätzen
IsoForge.exe analyze

:: Ein erzeugtes Abbild prüfen
IsoForge.exe verify --iso E:\Backups\IsoForge-full-PC-20260917-1042-7K3QD9WZ.iso
```

Alle Optionen zeigt `IsoForge.exe help`.

### Wiederherstellen

Das ISO in eine virtuelle Maschine einlegen oder – etwa mit Rufus im **DD-Modus** – auf einen
USB-Datenträger schreiben, davon starten. Es erscheint ein Menü:

1. **Vollständige Wiederherstellung** – der Zieldatenträger wird neu aufgeteilt und beschrieben.
2. **Parallel-Installation** – eine neue Partition im nicht zugewiesenen Bereich; das vorhandene
   System bleibt unangetastet, beim Start erscheint künftig eine Auswahl.
3. **Dateien in einen Ordner entpacken** – für einzelne Dateien aus einer Sicherung.
4. Datenträger anzeigen, Eingabeaufforderung, Neustart, Herunterfahren.

---

## Wie es funktioniert

```
  Laufendes Windows
        │
        ├─ 1. VSS-Schattenkopie          Win32_ShadowCopy.Create + Verzeichnis-Verweis
        │                                 darauf, damit DISM den Zustand lesen kann
        │
        ├─ 2. DISM /Capture-Image        install.wim, Ausschlüsse je nach Abstufung
        │                                 (bei Bedarf in install*.swm aufgeteilt,
        │                                  weil ISO 9660 keine Datei über 4 GiB kann)
        │
        ├─ 3. Startsystem                Winre.wim des Rechners wird zu boot.wim;
        │                                 winpeshl.ini startet darin Restore.cmd
        │
        ├─ 4. Startdateien               bootmgr, bootmgfw.efi, boot.sdi aus
        │                                 %SystemRoot%\Boot; BCD-Speicher per bcdedit
        │
        └─ 5. ISO schreiben              oscdimg, falls das Windows-ADK vorhanden ist,
                                          sonst der eingebaute Schreiber
```

### Warum kein Windows-ADK nötig ist

Die üblichen Anleitungen setzen das *Windows Assessment and Deployment Kit* voraus – für `oscdimg.exe`
(ISO schreiben) und für WinPE (Startsystem). IsoForge kommt ohne aus:

* **Startsystem:** Jedes Windows bringt eine Wiederherstellungsumgebung mit (`Winre.wim`). Das ist
  bereits ein vollständiges WinPE mit `diskpart`, `DISM` und `bcdboot`. IsoForge kopiert sie – mit
  Sicherungssemantik, denn die Datei gehört normalerweise nur `SYSTEM` – und ersetzt die
  Wiederherstellungsoberfläche über `winpeshl.ini` durch das eigene Skript.
* **ISO schreiben:** Enthalten ist ein eigener Schreiber für ISO 9660 (Level 2) mit Joliet-Namen und
  einem El-Torito-Startkatalog mit zwei Einträgen – einer für BIOS, einer für UEFI.
* **UEFI-Startabbild:** Der UEFI-Eintrag eines El-Torito-Mediums verweist auf ein FAT-Dateisystem, das
  die Firmware einhängt. Windows liefert dafür `efisys.bin` mit; fehlt sie, erzeugt IsoForge ein
  FAT16-Abbild mit `bootmgfw.efi` als `\EFI\BOOT\BOOTX64.EFI`.

Ist das ADK doch installiert, wird `oscdimg.exe` bevorzugt – das Ergebnis entspricht dann dem, was
Microsoft selbst erzeugt.

### Damit das Abbild auf fremder Hardware startet

Ein Abbild von echter Hardware bleibt in einer virtuellen Maschine sonst mit
`INACCESSIBLE_BOOT_DEVICE` stehen: Der Treiber für den neuen Speichercontroller ist zwar vorhanden,
aber nicht zum Systemstart freigeschaltet. Nach dem Schreiben des Abbilds lädt das
Wiederherstellungsskript deshalb die Registrierung des Zielsystems und setzt bei den bekannten
Speichertreibern `Start = 0` – AHCI, NVMe, die RAID-Controller der großen Hersteller sowie die
Treiber von Hyper-V, VMware, VirtIO/KVM und Xen.

Ein `sysprep /generalize` findet **nicht** statt: Es setzt einen Neustart voraus und würde der
Anforderung „während des laufenden Betriebs, 1:1" widersprechen. Der Weg über die Treiber-Freischaltung
erreicht dasselbe Ziel, ohne das System anzufassen.

Liegen im Ordner `drivers` auf dem Medium zusätzliche Treiber (`.inf`), werden sie beim Zurückspielen
automatisch eingespielt. Beim Erstellen füllt `--drivers <Verzeichnis>` diesen Ordner.

---

## Eindeutige Build-Kennung und Parallelbetrieb

Jeder Build bekommt eine eigene GUID. Daraus entsteht eine achtstellige, vorlesbare Kurzkennung
(Crockford-Base32, ohne `I`, `L`, `O` und `U`), zum Beispiel `7K3QD9WZ`. Diese Kennung taucht auf:

| Ort | Beispiel |
|---|---|
| Dateiname der Programmdatei | `IsoForge-1.0.0-7K3QD9WZ.exe` |
| Installationsverzeichnis | `C:\Program Files\IsoForge\7K3QD9WZ\` |
| Eintrag in der Softwareliste | `IsoForge 1.0.0 (7K3QD9WZ)` |
| Datenträgerbezeichnung des ISO | `ISOFORGE_FULL_20260917_7K3QD9WZ` |
| Starteintrag nach der Wiederherstellung | `Windows - IsoForge 7K3QD9WZ (2026-09-17 10:42)` |
| Vermerk im wiederhergestellten System | `C:\IsoForge\restored.txt` |

Dadurch:

* **Mehrere Programmstände nebeneinander.** `IsoForge.exe install` legt jeden Stand in ein eigenes
  Verzeichnis und trägt ihn als eigenen Eintrag in die Softwareliste ein. Eine spätere Fassung ersetzt
  die vorhandene nicht.
* **Mehrere Systemstände nebeneinander.** Über *Parallel-Installation* landet jede Sicherung in einer
  eigenen Partition mit einem eigenen, benannten Starteintrag. So lassen sich der Stand von letzter
  Woche und der von heute auf derselben Platte betreiben und beim Start auswählen.

---

## Selbst bauen

Nötig ist das [.NET 8 SDK](https://dotnet.microsoft.com/download). Gebaut wird unter Windows:

```powershell
.\build.ps1                                          # neue Kennung je Lauf
.\build.ps1 -BuildId 6f9619ff-8b86-d011-b42d-00cf4fc964ff   # reproduzierbar
```

Das Ergebnis liegt unter `artifacts\IsoForge-1.0.0-<Kennung>.exe`. Die Datei ist eigenständig; auf dem
Zielrechner wird kein .NET gebraucht.

**Ohne eigenen Windows-Rechner:** Der Arbeitsablauf `build` unter *Actions* baut die Programmdatei auf
einem Windows-Läufer und hängt sie als Artefakt an den Lauf. Unter *Run workflow* lässt sich eine feste
Build-GUID vorgeben.

### Aufbau

```
src/IsoForge.Media/     ISO-9660-Schreiber und -Leser, FAT16-Erzeuger (plattformunabhängig)
src/IsoForge/
  Core/                 Protokoll, Prozessaufrufe, Privilegien, Build-Kennung, Vorprüfungen
  Capture/              Schattenkopie, Abstufungen, DISM, Ablaufsteuerung
  Media/                Startdateien, BCD, WinPE-Aufbereitung, Medienaufbau
  Payload/              Skripte, die im Startsystem laufen (Batch – WinPE hat kein PowerShell)
tests/IsoForge.Tests/   Tests für Schreiber und FAT-Erzeuger
```

### Prüfungen

```powershell
dotnet test                       # Schreiber gegen unabhängige Leser geprüft
IsoForge.exe selftest             # baut ein kleines Medium und liest es zurück
```

Die Bauumgebung hängt das erzeugte Prüf-ISO zusätzlich mit `Mount-DiskImage` ein – Windows selbst
bestätigt damit, dass die Struktur stimmt.

---

## Grenzen und was man wissen sollte

* **Dauer und Platz.** Eine 1:1-Sicherung von 200 GB belegtem Speicher dauert mit `--compression max`
  je nach Rechner mehrere Stunden und braucht während des Laufs etwa das Doppelte des erwarteten
  Ergebnisses an freiem Platz. `--compression fast` ist deutlich schneller bei größerem Abbild.
  `IsoForge.exe analyze` schätzt beides vorab.
* **BIOS/MBR beim vollständigen Leeren.** Wird ein Datenträger komplett geleert, muss der Startcode im
  Master Boot Record neu geschrieben werden – dafür gibt es in WinPE nur `bootsect.exe`, das Windows
  nicht mitliefert. IsoForge sucht es beim Erstellen (ADK, `--bootsect <Pfad>`, z. B. `\boot\bootsect.exe`
  eines Windows-Installationsmediums) und legt es auf das Medium. Fehlt es, bietet das
  Wiederherstellungsmenü an, die vorhandene Aufteilung beizubehalten und nur die Zielpartition neu zu
  formatieren – der Startcode bleibt dann erhalten. **UEFI-Ziele sind davon nicht betroffen**, dort ist
  nichts weiter nötig.
* **Ziel-Datenträger groß genug.** Die Windows-Partition muss die entpackten Daten fassen; ein Abbild
  von 300 GB belegtem Speicher passt nicht auf eine 256-GB-SSD.
* **Parallel-Installationen sind Kopien.** Zwei Stände desselben Systems tragen denselben
  Computernamen und dieselbe Maschinen-SID. Für den Privatgebrauch und zum Vergleich zweier Stände ist
  das unproblematisch; in einer Domäne sollten beide nicht gleichzeitig angemeldet sein.
* **BitLocker.** Ein verschlüsseltes Laufwerk wird im Klartext gesichert, solange es entsperrt ist. Das
  erzeugte ISO ist damit ungeschützt – es gehört an einen sicheren Ort.
* **Lizenz.** Ein OEM-Windows ist an den Rechner gebunden, mit dem es geliefert wurde. Die eigene
  Installation zu sichern und wiederherzustellen ist gedeckt; sie auf fremde Hardware zu übertragen
  in der Regel nicht.
* **Verschlüsselte Benutzerdateien (EFS)** werden mitgesichert, lassen sich ohne das zugehörige
  Zertifikat nach der Wiederherstellung aber nicht öffnen.

---

## Lizenz

MIT – siehe [LICENSE](LICENSE).
