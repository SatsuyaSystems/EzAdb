# EzAdb

EzAdb ist eine Windows-App zur Verwaltung von Android-Geräten über ADB.

## Start

Das ZIP `EzAdb-win-x64.zip` aus den [GitHub-Releases](https://github.com/SatsuyaSystems/EzAdb/releases) vollständig entpacken und `EzAdb.exe` öffnen. Die .NET-Laufzeit ist enthalten. Richte ADB und scrcpy bei Bedarf beim ersten Start über **Automatisch einrichten** ein. Wähle links den gewünschten Bereich und oben rechts das aktive Gerät.

- **USB** – USB-Debugging in den Android-Entwickleroptionen aktivieren, das Gerät per USB anschließen und die Debugging-Freigabe auf dem Gerät bestätigen.
- **WLAN** – In EzAdb **QR-Code anzeigen** wählen. Auf Android unter **WLAN-Debugging → Gerät über QR-Code koppeln** scannen. Die Kopplung per sechsstelligen Code und die manuelle Verbindung über die Adresse von der WLAN-Debugging-Hauptseite stehen ebenfalls zur Verfügung. Unter **Gespeicherte WLAN-Geräte** kannst du die Netzwerkverbindung gezielt trennen und wiederherstellen. [ADB-Dokumentation](https://developer.android.com/tools/adb#wireless-android11-command-line)
- **APK-Installer** – Eine oder mehrere einzelne APK-Dateien auswählen oder im APK-Installer ablegen. Beim Installieren eines Updates bleiben die vorhandenen App-Daten erhalten.
- **Bildschirm** – **Ton am Gerät lassen** ist standardmäßig aktiv. So spielt Android weiter über seine verbundenen Lautsprecher oder Kopfhörer. Beim Umschalten speichert EzAdb die Auswahl und öffnet laufende Bildschirmfenster kurz neu. **Screenshot speichern** speichert ein PNG. [scrcpy-Audio](https://github.com/Genymobile/scrcpy/blob/master/doc/audio.md)

## Gerätefunktionen

- **Dateien** – Ordner per Doppelklick, Schnellzugriff oder Adressleiste öffnen. Mit **Alt+Links**, **Alt+Rechts** und **Alt+Hoch** durch Ordner navigieren. Dateien filtern, Unterordner durchsuchen und Einträge per Rechtsklick bearbeiten. Dateien aus Windows auf die Liste oder auf einen Ordner ziehen. Für die Gegenrichtung **Für Drag & Drop laden** wählen und die markierten Einträge anschließend in den Explorer ziehen.
- **Windows-Explorer** – Unter **Dateien → Windows-Explorer** öffnest du **Dieser PC** oder die Windows-Einstellungen für mobile Geräte. Per USB erscheint der Android-Speicher nach Aktivierung der USB-Dateiübertragung. Kabellos verwendet der Windows-Explorer **Link zu Windows** auf Windows 11 und Android 11 oder neuer. [Microsoft-Anleitung](https://support.microsoft.com/en-us/windows/experience/fileexplorer/setting-up-and-using-your-phone-in-file-explorer)
- **Apps** – Pakete des aktuellen Android-Nutzers nach Paket-ID oder verfügbarem App-Namen suchen. Apps starten, stoppen, aktivieren, deaktivieren oder deinstallieren. Der APK-Export speichert auch vorhandene Split-APKs.
- **Gerätemonitor** – Akku, Temperatur, CPU, RAM und gemeinsamen Speicher im Vier-Sekunden-Takt beobachten. Android-Version, API, ABI und weitere Systemdetails stehen unter den Diagrammen.
- **Studio** – MP4-Aufnahmen mit einstellbaren FPS, Bitrate und maximaler Kantenlänge erstellen. Geräteaudio wird ab Android 11 unterstützt. Wenn der Ton am Gerät bleiben soll, benötigt die Aufnahme Android 13 oder neuer. Live-Kameras und Kameravideos benötigen Android 12, virtuelle Displays Android 10. Kamera-PNGs verwenden FFmpeg aus PATH oder dem Ordner `tools`. [scrcpy-Aufnahme](https://github.com/Genymobile/scrcpy/blob/v5.0.1/doc/recording.md) · [Kamera](https://github.com/Genymobile/scrcpy/blob/v5.0.1/doc/camera.md) · [Virtuelle Displays](https://github.com/Genymobile/scrcpy/blob/v5.0.1/doc/virtual-display.md)
- **Fernbedienung und ADB-Konsole** – Gerätetasten und Texteingabe über ADB nutzen. Die Konsole führt Befehle mit Windows-Anführungszeichen aus, zeigt die Ausgabe live und merkt sich Befehle für die Sitzung.
- **Virtuelles Display mit App öffnen** – Unter **Bildschirm → Virtuelle Displays → App auswählen** nach einem App-Namen suchen und anschließend **Virtuelles Display öffnen** wählen. In der Liste der laufenden Studio-Sitzungen kannst du weitere Apps für das Display auswählen.
- **Makros** – Wähle **Bildschirm → Makro-Steuerung öffnen**, um Makros in einem separaten Fenster zu bearbeiten, zu starten oder zu stoppen. Tippen, Wischen, Texte, Tasten und App-Starts verwenden das jeweilige virtuelle Display. Freie ADB-Befehle gelten für das Gerät. Den Verbindungs-Autostart findest du unter **Bildschirm → Makrobibliothek**.
- **Auto-Klicker** – Wähle im Makrofenster **Klickpunkte setzen**, um direkt ins Livebild zu klicken. Pro Nummer bestimmst du Reihenfolge, Wartezeit vor jedem Druck, Haltedauer und Wiederholungen. Ein Klick auf die Nummer öffnet ihre Regeln, auch zum Versetzen oder Entfernen. Mit **Leiste versetzen** werden verdeckte Bildbereiche erreichbar. Nach dem Speichern kehrst du in die Makro-Steuerung zurück. Verwende beim Setzen die ungedrehte, ungespiegelte scrcpy-Ansicht und behalte die Android-Ausrichtung während des Ablaufs bei.
- **Mehrere Geräte** – Installationen, Dateiübertragungen, ADB-Befehle und Bildschirmfenster parallel starten.
- **Backups** – Zugängliche Android-Dateien in einen wählbaren Windows-Ordner sichern. Gesicherte Quellen oder einzelne Dateien lassen sich in ihre ursprünglichen Android-Ordner zurückspielen.

Android, Gerätehersteller und Arbeitsprofile bestimmen, welche Befehle, Paketänderungen, Dateien, Sensorwerte und Aufnahmen über ADB verfügbar sind. Geschützte App-Daten und DRM-Inhalte bleiben durch Android geschützt.

## Aus Quellcode starten

Mit dem .NET 8 SDK unter Windows `dotnet run` ausführen. EzAdb findet ADB und scrcpy über PATH oder im Unterordner `tools` neben der EXE. Im Einrichtungsdialog richtet **Automatisch einrichten** beide Tools ein. Falls ein Download nötig ist, prüft EzAdb die SHA-256-Prüfsumme des offiziellen Pakets und speichert die Tools im Benutzerprofil. **Tools verwalten** zeigt die verwendeten Ordner und öffnet sie im Explorer. [Offizielle scrcpy-Pakete](https://github.com/Genymobile/scrcpy/releases/tag/v5.0.1)

`dotnet publish -c Release -r win-x64 --self-contained true -p:DebugType=none -p:DebugSymbols=false -o artifacts/EzAdb-win-x64` erzeugt einen Windows-x64-Build mit Laufzeit.

EzAdb speichert Einstellungen, WLAN-Profile und Makros lokal im Benutzerprofil. Der automatische Tool-Download ruft das offizielle scrcpy-Paket auf GitHub ab.

`Start-Process .\artifacts\EzAdb-win-x64\EzAdb.exe -ArgumentList '--self-test' -Wait -PassThru` führt den Selbsttest aus. Exit-Code `0` bedeutet Erfolg.
