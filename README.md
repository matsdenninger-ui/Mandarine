# Mandarine 🍊 – Zero-Click-Komprimierung für E-Mail-Anhänge (Windows)

Windows-Pendant zu „Tangerine“: läuft unsichtbar im Infobereich, und Dateien werden beim Ziehen mit gedrückter **Umschalttaste (⇧)** automatisch so verkleinert, dass sie als Anhang durchgehen. Alles passiert lokal auf dem PC – keine Cloud, keine externen APIs.

## Bedienung

| Aktion | Ergebnis |
|---|---|
| Dateien/Ordner mit **⇧** ziehen | Neben dem Mauszeiger erscheint eine Ablagefläche. Loslassen → **für E-Mail komprimieren** (Standard ≤ 17 MB). |
| Mit **⇧ + Alt** loslassen | Schwebendes Menü am Cursor: Komprimieren, JPEG/PNG, Bilder → PDF, Metadaten entfernen, Schwärzen, ZIP. Tasten **1–8**, **Esc** schließt. |
| Rechtsklick → **Senden an → Mandarine …** | Dasselbe ohne Ziehen (wird beim ersten Start eingerichtet). |
| Klick aufs Tray-Symbol | Dateiauswahl → komprimieren. Rechtsklick: Einstellungen. |

Nach jeder Aktion: dezenter Systemton, Toast unten rechts, **Ergebnis in der Zwischenablage** (Strg+V fügt es in Outlook als Anhang ein) und **im Explorer markiert**. Originale werden nie verändert; Ergebnisse landen als `Name_email.jpg` daneben (oder in `Downloads\Mandarine`, wenn der Ordner nicht beschreibbar ist).

## Was beim Komprimieren passiert

1. **Verlustfreier Durchgang:** EXIF/GPS, XMP, IPTC und Kommentare werden aus JPEG/PNG entfernt, ohne die Bilddaten anzufassen (Ausrichtung bleibt erhalten). HEIC/WebP/AVIF/TIFF/BMP werden in JPEG (oder PNG bei Transparenz) umgewandelt. Passt es danach, ist man fertig.
2. **Qualitätsleiter** für Bilder, PDFs und Office-Dateien, Stufe für Stufe von sanft nach kräftig:
   - Bilder: Farbprofil → sRGB, lange Kante 3200 → 800 px, JPEG-Qualität 85 → 56.
   - PDFs: eingebettete Fotos mit 200 → 96 dpi neu kodiert (Text bleibt markierbar); erst wenn das nicht reicht, wird die PDF als Bild-PDF neu aufgebaut.
   - Word/PowerPoint/Excel: Bilder in `*/media/` werden verkleinert, Dateinamen bleiben gleich.
3. **Videos** bekommen das verbleibende Budget, nach Laufzeit aufgeteilt, und werden mit dem Windows-eigenen Media-Foundation-Encoder (Hardware-beschleunigt) in H.264/AAC-MP4 umgewandelt. 60-fps-Clips werden auf 30 fps halbiert, die Auflösung folgt der Bitrate (1080p → 480p).
4. **Ordner, ZIPs und mehr als 10 Dateien** werden zu einem ZIP gebündelt. Enthaltene ZIPs werden ausgepackt, deren Inhalt mitkomprimiert und wieder gepackt.

Warum 17 MB statt 25 MB? Mails werden Base64-kodiert verschickt (+ ~37 %). 17 MB Dateien ergeben ≈ 24 MB auf dem Server – das passt bei Gmail, Outlook.com und iCloud. Für Firmen-Exchange (oft 10 MB) gibt es im Tray-Menü die Stufe „≤ 6 MB“.

## Berechtigungen (Unterschied zu macOS)

Windows braucht **keine** Freigabe wie „Bedienungshilfen“ oder „Eingabeüberwachung“:

- **Globale ⇧-Erkennung:** Low-Level-Maus-Hook (`SetWindowsHookEx(WH_MOUSE_LL)`) plus `GetAsyncKeyState` – läuft als normaler Benutzer. Einschränkung: Ziehvorgänge aus Fenstern, die *als Administrator* laufen, sieht ein normaler Prozess nicht (UIPI).
- **Drag & Drop, Zwischenablage, Explorer-Markierung:** Standard-Shell-APIs (`CF_HDROP`, `SHOpenFolderAndSelectItems`).
- **Autostart / „Senden an“:** nur im Benutzerprofil (`HKCU\…\Run`, `%APPDATA%\Microsoft\Windows\SendTo`).
- **SmartScreen:** Eine selbst gebaute, unsignierte `.exe` löst beim ersten Start „Der Computer wurde durch Windows geschützt“ aus → *Weitere Informationen → Trotzdem ausführen*. Für die Weitergabe an andere: Code-Signing-Zertifikat verwenden.

Optionale Windows-Komponenten (kostenlos, meist schon vorhanden):
- **HEIF-Bilderweiterungen** (Microsoft Store) für iPhone-Fotos (.heic).
- **HEVC-Videoerweiterungen** zum *Lesen* von iPhone-Videos (HEVC). Ausgegeben wird immer H.264, das jeder Empfänger abspielen kann.
- **Sprachpaket mit Texterkennung (OCR)** für die Schwärzen-Funktion – bei deutschem Windows normalerweise installiert.

## Bauen

Voraussetzung: Windows 10 (1809+) oder 11, [.NET 10 SDK](https://dotnet.microsoft.com/download) (`winget install Microsoft.DotNet.SDK.10`).

```powershell
.\build.ps1              # → publish\win-x64\Mandarine.exe (eigenständig, kein .NET auf dem Ziel-PC nötig)
.\build.ps1 win-arm64    # für ARM-Geräte
```

Oder ohne lokale Installation: Ordner in ein GitHub-Repository hochladen – der Workflow in `.github/workflows/build.yml` baut die `.exe` für x64 und ARM64 automatisch (Reiter *Actions* → *Artifacts*).

Zum Entwickeln: `src/Mandarine/Mandarine.csproj` in Visual Studio 2022/2026 oder VS Code (C# Dev Kit) öffnen und F5.

## Projektstruktur

```
src/Mandarine/
├─ App.xaml(.cs)              Hintergrund-Agent: verdrahtet Tray, Hook, Ablagefläche, Jobs, „Senden an“
├─ Settings.cs                %APPDATA%\Mandarine\settings.json
├─ SingleInstance.cs          eine Instanz pro Benutzer, Named Pipe für „Senden an“-Aufrufe
├─ Native/
│  ├─ GlobalDragWatcher.cs    WH_MOUSE_LL-Hook: Maustaste + Bewegung + ⇧ → Ablagefläche zeigen
│  ├─ WindowEffects.cs        Acrylic (Windows 11), runde Ecken, DPI-genaue Platzierung
│  ├─ ShellIntegration.cs     Senden an, Autostart, Explorer-Markierung, Zwischenablage
│  └─ Win32.cs                P/Invoke
├─ Core/
│  ├─ EmailPipeline.cs        Budget-Logik: verlustfrei → Qualitätsleiter → Videobudget
│  ├─ ActionRunner.cs         Menü-Aktionen (Konvertieren, Metadaten, Schwärzen, ZIP)
│  ├─ InputExpander.cs        Ordner/ZIPs → Dateiliste mit relativen Pfaden
│  ├─ OutputPlanner.cs        Zielordner, Dateinamen, ZIP-Bündelung
│  └─ Models.cs               Datentypen, Dateiarten, Größenformat
├─ Processing/
│  ├─ ImageCodec.cs           WIC/WPF: Dekodieren, sRGB, EXIF-Drehung, Skalieren, JPEG/PNG
│  ├─ MetadataStripper.cs     verlustfreies Entfernen von EXIF/XMP/IPTC (JPEG, PNG)
│  ├─ ImageCompressor.cs      Bild-Leiter
│  ├─ VideoCompressor.cs      MediaTranscoder (H.264/HEVC), Bitrate aus Zielgröße
│  ├─ PdfCompressor.cs        PDFsharp: Bilder in PDFs neu kodieren; Metadaten
│  ├─ PdfRasterizer.cs        Windows.Data.Pdf: Seiten rendern (Bild-PDF, Seiten → JPEG)
│  ├─ MiniPdfWriter.cs        schreibt Bild-PDFs
│  ├─ OfficeCompressor.cs     Bilder in .docx/.pptx/.xlsx
│  ├─ Redactor.cs             Windows-OCR + Muster (E-Mail, IBAN, Telefon, Karten) → schwarze Balken
│  └─ ZipBuilder.cs           ZIP, bereits komprimierte Medien werden nur gespeichert
└─ UI/
   ├─ DropZoneWindow.xaml     Ablagefläche am Cursor
   ├─ ActionMenuWindow.xaml   Optionen-Menü
   ├─ ToastWindow.xaml        Fortschritt/Ergebnis
   ├─ TrayIcon.cs             Infobereich-Symbol und Schnelleinstellungen
   ├─ JobRunner.cs            Warteschlange, Zwischenablage, Explorer, Ton
   └─ Theme.xaml / Theme.cs   Hell/Dunkel nach Windows-Einstellung
```

Einzige externe Abhängigkeit: [PDFsharp](https://github.com/empira/PDFsharp) (MIT). Bild, Video, PDF-Rendering und OCR kommen aus Windows selbst.

## Einstellungen

Die meisten Schalter sitzen im Tray-Menü. Alles weitere in `%APPDATA%\Mandarine\settings.json` (Tray → *Einstellungen bearbeiten*, danach *neu laden*):

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `TargetMegabytes` | 17 | Ziel für alle Anhänge zusammen |
| `StripMetadata` | true | GPS/Kamera/Autor entfernen |
| `Output` / `OutputFolder` | `NextToOriginal` | oder `Folder` = immer in `OutputFolder` |
| `Suffix` | `_email` | Namenszusatz |
| `ZipWhenMoreThan` | 10 | ab wie vielen Dateien gezippt wird |
| `PreferHevc` | false | HEVC statt H.264 (kleiner, aber nicht überall abspielbar) |
| `DropZone` | `NearCursor` | oder `TopCenter` (oben mittig wie eine Menüleiste) |
| `DragSourceApps` | Explorer, Browser, Outlook … | aus welchen Programmen ⇧-Ziehen die Fläche öffnet; `"*"` = alle |

Protokoll: `%LOCALAPPDATA%\Mandarine\log.txt`.

## Grenzen

- Anhänge, die man direkt **aus Outlook** zieht, sind virtuelle Dateien (kein Dateipfad) – erst speichern, dann ziehen.
- .7z/.rar werden unverändert übernommen (nur ZIP wird neu gepackt).
- Schwärzen basiert auf Texterkennung: Ergebnis vor dem Versand kurz ansehen. Geschwärzte PDFs sind Bild-PDFs, damit unter den Balken wirklich nichts mehr steht.
- Ein Video, das selbst bei ~220 kbit/s nicht ins Limit passt, wird nicht verunstaltet – Mandarine meldet das und empfiehlt einen Freigabe-Link.

## Testliste nach dem ersten Build

Der Code wurde ohne Windows-Compiler geschrieben; bitte einmal durchgehen:

1. Start → Tray-Symbol und Willkommens-Toast erscheinen; Windows 11 zeigt Acrylic-Hintergrund, Windows 10 eine helle Fläche.
2. Im Explorer 3 Fotos markieren, ziehen, ⇧ drücken → Ablagefläche am Cursor; loslassen → Toast, Strg+V in Outlook fügt sie ein.
3. iPhone-Foto (.heic) und ein hochkant aufgenommenes JPEG: Ergebnis steht richtig herum, Eigenschaften → Details zeigen kein GPS mehr.
4. Hochkant-Handyvideo (> 25 MB): Ergebnis-MP4 liegt unter dem Limit und wird **aufrecht** abgespielt.
5. Gescannte PDF (> 25 MB) und eine PowerPoint mit vielen Fotos.
6. Mit ⇧ + Alt loslassen → Menü; *Sensible Daten schwärzen* auf einem Screenshot mit E-Mail-Adresse.
7. Rechtsklick → Senden an → *Mandarine – Optionen*.
