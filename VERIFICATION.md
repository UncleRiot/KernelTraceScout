# Prüfbericht — 27.10.2026

**Status: Implementierter, kompilierter Prüfstand. Keine produktionsreife Endfassung.**
Die Abnahmekriterien sind erst nach den unten genannten Windows-Laufzeittests nachgewiesen.

## Implementierte Funktionen

- Prozessliste einschließlich ruhender, neuer und unzugänglicher Prozesse; CPU-Werte aus
  prozessbezogenen Intervallen einer monotonen Uhr, normiert über alle aktiven Prozessorgruppen.
- Manuelles Refresh, optionales Auto-Refresh, XML-Einstellungen und vier Bewertungszustände.
- Mindestlast, absolute Kernelzeit, Kernel/User-Verhältnis, Messzahl, Dauer und Wiederholung.
- Native ETW-Sitzung mit CPU-Profiling, StackWalk, Prozess- und Image-Ereignissen.
- Native ETL-Verarbeitung über typisierte EVENT_RECORD/EVENT_TRACE_LOGFILEW-Strukturen.
  Versionsabhängige Image-Felder werden über Windows TDH gelesen.
- Exakte Korrelation von Sample und Stack über Rohzeitstempel und Thread-ID; Zuordnung zu
  Modulbereichen unter Berücksichtigung von Entladezeiten und mehrdeutigen Bereichen.
- Treibertabelle in der Anwendung: inklusive Samples, Anteil, Instruktionsadressen-Treffer,
  auffällige Prozesse, zeitliche Wiederholung und nachvollziehbare Erklärung.
- Auswahl eines Treibers zeigt PID-Zuordnungen und exemplarische Stackketten. Prozessübersicht
  zeigt beobachtete Module nur bei passender Prozessidentität. Der ETW-Treiberhinweis steht
  direkt neben Prozess/PID und unterscheidet direkte CPU-Sample-Treffer von bloßer Stack-Beteiligung.
- Prozesshinweise markieren einen Modulnamen, der mit einem aktuell geladenen Minifilter-Namen
  übereinstimmt. Diese Namensübereinstimmung und Stack-Samples sind Hinweise, kein Kausalbeweis.
- Englischer Berechtigungsdialog mit Neustart als Administrator, eingeschränktem Betrieb und Exit.
- Beibehalten: Minifilteranzeige, CSV-Export, WinForms und .NET Framework 4.8 als Standardziel.
- Optionaler .NET-10-Build aus derselben Codebasis, ohne Drittanbieterpakete.

## Behobene Fehler und Sicherheitsprobleme

- Vorher vollständig gesperrte ETW-Pfade durch implementierte, begrenzte Aufzeichnung/Auswertung ersetzt.
- Falsche feste EVENT_RECORD-/Logfile-Speicheroffsets entfernt; typisierte SDK-Abbildungen mit
  Startprüfung der Größen und relevanten Offsets. Insbesondere liegt der x64-Record-Callback
  bei 424, nicht beim bisherigen fest codierten Offset 400.
- Kein geratenes Image-Dateinamensfeld mehr. TDH-Fehler werden als Datenlücke ausgewiesen.
- Stack-Ereignisse ohne passendes CPU-Sample werden nicht als CPU-Nachweis gewertet.
- Rekursive Frames und getrennte Stack-Ereignisse zählen höchstens einmal je CPU-Sample/Modul.
- Reines Auftreten im Stack wird von tatsächlichen Instruktionsadressen-Treffern getrennt.
- Null-CPU-Prozesse werden nicht mehr ausgeblendet. Infinity bei winziger Last löst keinen Verdacht aus.
- Threading: Prozessabfragen und ETW-Arbeit im Hintergrund, UI-Änderungen auf dem GUI-Thread;
  keine überlappenden Prozessabfragen; Abschluss und Schließen berücksichtigen laufende Aufgaben.
- Sitzungseigentum erst nach erfolgreichem StartTrace; kein Stoppen einer bereits vorhandenen Sitzung.
- Zusätzlicher eigener Watchdog für GUI-Abbruch; fester Sitzungsname, zufällige GUID und ursprünglicher
  Sitzungshandle. Keine fremden Prozesse/Dienste/Treiber werden beendet oder umkonfiguriert.
- Temporäres Profiling-Privileg im eigenen Token mit Wiederherstellung; keine Richtlinienänderungen.
- Zeit-, Datei-, Ereignis-, Frame-, Modul- und Evidenzlimits; Retention und Logrotation.
- Minifilter-Strukturlängen, verkettete Einträge und ungültige Handles werden geprüft.
- XML ohne DTD/externen Resolver, Größen-/Bereichsprüfung; atomarer Konfigurationsaustausch.
- Fehler beim Schreiben eines Berichts verwerfen die bereits berechneten GUI-Ergebnisse nicht.

## Tatsächlich ausgeführte Prüfungen

Die Prüfungen unten stammen vom zuvor vorliegenden Code-Stand. Die spätere UI-Anpassung
(Position des Prozess-Treiberhinweises, Rücksprung zur Prozessliste nach ETW und Minifilter-Namenshinweis)
ist damit nicht kompiliert oder unter Windows ausgeführt. Ein erneuter Build-Versuch in der aktuellen
Arbeitsumgebung war nicht möglich, weil `dotnet` dort nicht installiert ist.

Umgebung: Ubuntu 24.04 x64; .NET SDK 10.0.403 / MSBuild 18.9.15.

| Prüfung | Ergebnis |
|---|---|
| Release/x64 Solution-Cross-Build gegen .NET Framework 4.8 | Bestanden, 0 Fehler, 0 Warnungen |
| Optionaler Release-Build `BuildNet10=true` | Bestanden, 0 Fehler, 0 Warnungen |
| Separates .NET-4.8-Testprogramm kompiliert | Bestanden |
| 27 deterministische Assertions | Alle bestanden |
| Marshal-Größen/Offsets der x64-Strukturen unter .NET 10/Linux | Bestanden; kein Windows-ABI-Laufzeitnachweis |
| C#-Syntaxprüfung aller sechs Quelldateien mit Tree-sitter | Bestanden |
| XML von Manifest, Einstellungen und Builddateien | Bestanden |
| ZIP-CRC, flache Dateiliste und Bytevergleich mit Ausgabedateien | Bei Paketierung geprüft |

Das .NET-4.8-Testprogramm wurde mit einem temporären .NET-10-Runtimeconfig unter Linux ausgeführt.
Es ruft im Testpfad keine Windows-DLL auf. Dies prüft die reale C#-Auswertungslogik und das
Marshalling-Layout im vorhandenen Runtime, ausdrücklich nicht .NET Framework unter Windows.
Die Test-Fixtures gelangen nicht in die Anwendung und erzeugen dort keine simulierten Messwerte.
`TEST-RESULTS.txt`, `BUILD-NET48.txt` und `BUILD-NET10.txt` enthalten die realen Ausgaben.
MSBuild lief mit `-m:1`, weil paralleler Solution-Restore in dieser Umgebung ohne konkrete
Compilerdiagnose abbrach. Die sequentielle Solution-Kompilierung war erfolgreich.

## Konkrete offene Freigabepunkte

1. Auf Windows x64 das SDK-Probeprogramm `VerifyEtwLayouts.cpp` kompilieren und die managed
   Tests unter dem tatsächlichen .NET Framework 4.8 ausführen.
2. StartTrace/TraceSetInformation/ControlTrace, TDH und ProcessTrace auf den vorgesehenen
   Windows-/Server-Versionen mit echten ETLs ausführen. Erfolg und Nutzbarkeit der Aufzeichnung
   sind hier nicht durch einen Windows-Lauf belegt.
3. Die Auswertung mit bekannten CPU-Lastfällen validieren: belasteter Treiber, bloßer Aufrufer,
   unvollständige Stacks, fehlende Images, Image-Unload, Prozessende/PID-Wiederverwendung und
   Verlustereignisse. Erkennungsrate und False-Positive-Rate sind nicht kalibriert.
4. GUI einschließlich Skalierung, Tabellen, englischem Admin-Dialog, UAC-Abbruch,
   manueller/automatischer Aktualisierung und Schließen während Start/Stop/Analyse prüfen.
5. Dateigröße, Aufzeichnungsende, volle/read-only Datenträger, Sitzungs-Konflikte, große
   Prozesszahlen, hohe Kernzahlen und wiederholte Start-/Stop-Zyklen unter Windows testen.
6. Erzwungenen GUI-Abbruch und Watchdog-Bereinigung testen. Werden beide Prozesse beendet,
   ist eine Sitzung nicht garantiert sofort bereinigt; ihr sequenzielles Dateilimit bleibt gesetzt.
   Auch der Start-/Watchdog-Übergang besitzt ohne Windows-Fehlerinjektion keinen Ausfallnachweis.
7. .NET 10 unter Windows einschließlich EXE-Apphost und Watchdog testen.

Weitere technische Grenzen: keine Symbolauflösung; Ausgabe als Modul+Offset. Keine kausale
Beweisführung aus Sampling. Fehlende oder ausgelagerte Kernelstacks können eine Diagnose
verhindern; die Anwendung verändert deswegen keine Registry-Einstellung. CPU-Arbeitsbudgets
sind kooperativ, keine harte Betriebssystem-CPU-Quote. Native APIs können länger blockieren;
Windows kann angeforderte ETW-Puffergrößen/-zahlen anpassen. Es gibt keine absolute Garantie
gegen Betriebssystem- oder Treiberprobleme.

## Herangezogene Herstellerquellen

- [EVENT_TRACE_LOGFILEW](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_logfilew)
- [EVENT_RECORD](https://learn.microsoft.com/en-us/windows/win32/api/evntcons/ns-evntcons-event_record)
- [EVENT_HEADER](https://learn.microsoft.com/en-us/windows/win32/api/evntcons/ns-evntcons-event_header)
- [EVENT_TRACE und EVENT_TRACE_HEADER](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace)
- [TRACE_LOGFILE_HEADER](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-trace_logfile_header)
- [EVENT_TRACE_PROPERTIES](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_properties)
- [StartTraceW](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-starttracew)
- [ControlTraceW](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-controltracew)
- [TraceSetInformation](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-tracesetinformation)
- [TRACE_QUERY_INFO_CLASS](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ne-evntrace-trace_query_info_class)
- [StackWalk_Event](https://learn.microsoft.com/en-us/windows/win32/etw/stackwalk-event)
- [SampledProfile](https://learn.microsoft.com/en-us/windows/win32/etw/sampledprofile)
- [Image_Load](https://learn.microsoft.com/en-us/windows/win32/etw/image-load)
- [TdhGetProperty](https://learn.microsoft.com/en-us/windows/win32/api/tdh/nf-tdh-tdhgetproperty)
- [PROPERTY_DATA_DESCRIPTOR](https://learn.microsoft.com/en-us/windows/win32/api/tdh/ns-tdh-property_data_descriptor)
- [Microsoft system-logger sample](https://github.com/microsoft/Windows-driver-samples/blob/main/general/tracing/SystemTraceControl/SystemTraceControl.cpp)
- [Logging modes](https://learn.microsoft.com/en-us/windows/win32/etw/logging-mode-constants)
- [AdjustTokenPrivileges](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-adjusttokenprivileges)
- [FilterFindFirst](https://learn.microsoft.com/en-us/windows/win32/api/fltuser/nf-fltuser-filterfindfirst)
- [FILTER_FULL_INFORMATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/fltuserstructures/ns-fltuserstructures-_filter_full_information)

## Korrektur nach Windows-Fehlermeldung „Image lifetime budget exceeded“

Die bereitgestellte Windows-Logdatei dokumentiert zwei Abbrüche am 27.10.2026 um
10:40:49 und 10:41:09. Die vorherige Implementierung speicherte auch User-Space-Images
aller Prozesse in der auf 20.000 Einträge begrenzten Modulliste.

Korrektur: User-Images werden vor weiterer TDH-Abfrage verworfen; die vorhandene
Kernel-Adressklassifikation ist maßgeblich. Wiederholte Kernel-Rundown-Einträge
werden global nach Basis, Größe und Pfad zusammengefasst. Echte Entlade-/Ladevorgänge
bleiben getrennte Lebenszeiten. Das harte Ressourcenlimit bleibt bestehen. Der Dialog
zeigt bei einem weiteren Abbruch jetzt auch die konkrete innere Fehlermeldung.

Sechs zusätzliche Regressionstests prüfen mehr als 20.000 User-Images, wiederholte
Kernel-Rundowns, globale Entladung, erneutes Laden, End-Rundown und das harte Limit.
Die erneute echte ETW-Aufzeichnung unter Windows steht weiterhin aus.
