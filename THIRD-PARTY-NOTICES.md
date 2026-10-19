# Lizenzhinweise

Die vollständigen Lizenzhinweise aller Bausteine, die in `Zytrus.exe` stecken, stehen in
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt). Diese Datei gehört bei jeder Weitergabe
der `.exe` dazu.

| Baustein | Lizenz | Urheber |
|---|---|---|
| PDFsharp **6.2.4** (inkl. BigGustave, Public Domain) | MIT | empira Software GmbH |
| liquid-glass-js (Glas-Effekt, nach C# portiert in `src/Zytrus/UI/LiquidGlass.cs`) | MIT | Armagan Amcalar |
| .NET 10 Runtime, WPF, Windows Forms | MIT (+ Drittanbieter-Hinweise) | .NET Foundation and Contributors |
| C#/WinRT, Windows-SDK-Anbindung | MIT | Microsoft Corporation |

**Wichtig:** PDFsharp ab Version 6.5 steht unter AGPL oder kommerzieller Lizenz. Die Version ist in
`src/Zytrus/Zytrus.csproj` fest auf 6.2.4 eingetragen und sollte nicht erhöht werden.
