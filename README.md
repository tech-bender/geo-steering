# GordonGeoSteer

Gordon Technologies' replacement for the end-of-life Schlumberger Pathfinder PZS 5.2.0
directional-drilling / geosteering application. Original code, written against documented
PZS file formats (see the project's reverse-engineering notes) - no PZS code is copied.

**v1 scope: import + browse only.** Open a PZS project folder, import it, and browse wells,
curves, surveys, targets, the geology model and the file inventory. No survey calculation,
geosteering editing or real-time feed yet.

## Layout

```
GordonGeoSteer.sln
build.cmd                     builds both parts (importer, then app)
selftest.cmd                  end-to-end self-test against a real PZS folder + value checks
scripts/get-dotnet-sdk.ps1    installs the .NET 8 SDK into .dotnet\ (repo-local, no admin)
tools/PzsImporter/            import engine (.NET Framework 3.5, x86, plain csc.exe build)
  PzsImporter.cs              PZS folder -> gordon.geosteering.project v1.0 JSON
  build.cmd                   -> tools/PzsImporter/bin/PzsImporter.exe
  reference/PzsCfgDump.cs     earlier .pzscfg object-graph dumper (binder/AssemblyResolve reference)
src/GordonGeoSteer/           the application (.NET 8 WPF)
  Model/ProjectDocument.cs    POCOs mirroring the JSON schema (System.Text.Json)
  Services/ImporterRunner.cs  runs PzsImporter.exe as a child process
  Services/ProjectJson.cs     loads project.json + data/*.json
  Services/DebugDump.cs       Help > Dump loaded data (view-model contents -> text)
  Services/SelfTest.cs        --selftest mode (visit every view, dump, screenshots)
  ViewModels/                 tree + detail view-models
  Controls/                   CurvePlot (MD vs value), GridHeatmap (bed grid), DataGrid helper
tests/Verify-BakkenDump.ps1   asserts known Bakken sample values in a view-model dump
```

### Why two parts

`.pzscfg` model files are .NET `BinaryFormatter` snapshots of PZS's own classes, so reading
them needs .NET Framework, x86, and PZS 5.2.0's own (non-licensing) assemblies loaded through a
strict allow-list binder. That is isolated in `PzsImporter.exe`. The WPF app never touches
`BinaryFormatter`, PZS assemblies or anything licensing-related - it only runs the importer as a
separate process and reads the clean JSON it writes.

## Requirements

- Windows, with **.NET Framework 3.5** enabled (Windows Features) - for `csc.exe` v3.5 and to run the importer.
- **.NET 8 SDK** to build the app (or run `scripts\get-dotnet-sdk.ps1` to put one in `.dotnet\`).
  Running the built app needs only the .NET 8 Desktop Runtime.
- To import `.pzscfg` files: PZS 5.2.0 installed at `C:\Program Files (x86)\Schlumberger\PZS 5.2.0`
  (or pass `--pzs-dir=` to the importer). It is only read, never modified.
- 32-bit Microsoft Access (Jet) ODBC driver (`Microsoft Access Driver (*.mdb)`, ships with Windows) for `.pzsdb`.

## Build

```
build.cmd            (Release; or: build.cmd Debug)
```

Output: `src\GordonGeoSteer\bin\Release\net8.0-windows\GordonGeoSteer.exe`, with the importer
copied alongside as `PzsImporter\PzsImporter.exe`.

## Run

- Interactive: start `GordonGeoSteer.exe`, then **File > Open PZS Project Folder (import)...**.
  The importer's output goes to `%TEMP%\GordonGeoSteer\imports\<project>_<timestamp>\`
  (`project.json`, `data\`, `opaque\`). **File > Open Imported project.json...** reopens one.
- Command line: `GordonGeoSteer.exe --import "<PZS folder>" [--out <dir>]` or `--open <project.json>`.
- Importer alone: `tools\PzsImporter\bin\PzsImporter.exe "<PZS folder>" <outDir> [--pzs-dir=<PZS install>]`
  - exit 0 OK, 2 usage/protected path/bad source, 3 cross-check failure, 4 fatal error.
  - It refuses to write under the source folder or any Schlumberger install/data folder.

Import errors are never swallowed: a non-zero importer exit shows a red banner and the full
importer log; failed cross-checks (exit 3) still load but are flagged in red, and listed in
**Import report**.

## Self-test

```
selftest.cmd "<PZS project folder>" [reportDir]
```

Runs the app with `--selftest`: imports through the same code path as the UI, selects every tree
node (building and rendering every view), and writes to `selftest-output\report\`:
`selftest.log`, `viewmodel-dump.txt` (what the view-models hold), and `ui_*.png` window renders.
For the Bakken P2-H sample it then runs `tests\Verify-BakkenDump.ps1`, which checks known real
values (RDAH_M depth range, KOP/Landing_Pt/PBHL targets, Bakken bed names, survey counts, ...).

## Not in v1 (deliberately)

Survey calculation (minimum curvature, N/E/TVD), geosteering editing, resistivity modeling,
real-time (WITSML/OPC) feed, LAS import, map view, editing/saving any data.
