# GordonGeoSteer

Gordon Technologies' replacement for the end-of-life Schlumberger Pathfinder PZS 5.2.0
directional-drilling / geosteering application. Original code, written against documented
PZS file formats (see the project's reverse-engineering notes) - no PZS code is copied.

**v1:** import + browse. Open a PZS project folder, import it, and browse wells, curves, surveys,
targets, the geology model and the file inventory (**Browse** tab).

**v2: steering view** (**Steering view** tab). It has a curtain section with bed boundaries and the
wellbore, vertical log tracks on a TVT axis, horizontal log tracks above the curtain, per-curve styling
and shading, user bed boundaries, and a Gordon workspace file. It runs on a new engine library that
covers minimum curvature, bicubic bed surfaces, geomod shifts, TVT and synthetic logs. There is no
real-time feed or geomod editing yet.

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
src/GordonGeoSteer.Engine/    v2 engine (.NET 8 class library, no WPF)
  Survey/                     minimum curvature, interpolation at any MD, vertical section
  Geology/                    natural spline / bicubic bed surfaces, Hermite geomod path, faults, depth-index model
  Steering/                   SteeringProject (from project.json), SteeringModel (TVT, synthetic logs, curtain profile)
  Display/                    independent axis zoom math, min/max-per-pixel decimation
  Workspace/                  Gordon workspace JSON (gordon.geosteering.workspace v1)
  Validation/                 EngineValidation: closed-form arcs + comparisons against PZS's own stored results
  Model/, Io/                 ProjectDocument / ProjectJson (moved here from the app; namespaces unchanged)
src/GordonGeoSteer/Controls/Steering/   CurtainControl, LogTrackControl, TvtAxisControl, TrackPainter (DrawingContext)
src/GordonGeoSteer/ViewModels/Steering/ SteeringViewModel, track/curve/bed view-models
src/GordonGeoSteer/Views/SteeringView.xaml  layout + properties panel
src/GordonGeoSteer/Services/SteeringSelfTest.cs  steering part of --selftest
tests/GordonGeoSteer.Engine.Tests/      xunit tests (synthetic cases + real-data validation if an import exists)
nuget.config                  repo-local package source (xunit for the tests only)
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

The self-test also covers the steering view. It writes `ui_steer_01_default` to `ui_steer_06_reopened`
PNGs, which show the default layout, a layout using every line style and every shading mode with user
boundaries, horizontal-only zoom, vertical-only zoom, DI-stretch mode, and the view after a workspace
save and project reopen. It checks independent zoom, track alignment and the workspace round trip, and
writes `engine-validation.txt`. The steering workspace goes to the report folder, not your own
workspace store. Set `GG_CONFIG=Debug` to self-test a Debug build.

## Unit tests

```
.dotnet\dotnet test tests\GordonGeoSteer.Engine.Tests
```

The synthetic tests cover:
- closed-form arcs, the ratio-factor series, dogleg and vertical section
- splines and bicubic surfaces, Hermite and decay geomod curves, fault ramp and fault table
- the depth-index model and its truncation rules
- TVT direction: down-structure plots down, up-structure plots up, parallel stacks (also on a dipping bed)
- synthetic log equals the offset log at the same TVT; marker reference beds
- zoom gestures, decimation and workspace round trip

`RealDataValidationTests` also runs against an imported project. Set `GG_PROJECT_JSON`, or run
`selftest.cmd` first so `selftest-output\import\project.json` exists. Without one, it passes and
says it did not run. No real well data is stored in the repo.

## Steering view

Open a project, then use the **Steering view** tab (or **Steering > Show steering view**).

- **Curtain section** (centre left): x = vertical section (project `azimuthVS`/origin), y = TVD.
  - It draws imported grid beds and markers through the depth-index model, user boundaries, the
    wellbore with survey stations, and geomod picks (red triangles).
  - Wheel zooms both axes at the cursor; **Ctrl+wheel** zooms horizontally only; **Shift+wheel**
    zooms vertically only.
  - Drag pans. Drag on the TVD axis or the VS axis zooms that axis. Double-click resets. The toolbar
    also has zoom buttons.
- **Horizontal tracks** (above the curtain): these share the curtain's VS axis object and pixel margins,
  so they always line up with it. They show realtime, synthetic and PZS-model curves along the lateral.
  An offset curve placed here is shown as its synthetic.
- **Vertical tracks** (right): y = TVT, with the reference bed at 0, plus a TVT axis. They show offset
  curves at the offset well's TVT and realtime curves at each sample's own TVT. Bed boundaries are
  drawn across the tracks with labels.
- **Properties panel**:
  - Tracks: add or remove tracks; set title, size, scale min/max and log scale.
  - Curves: add or remove curves; set colour, thickness, line style (solid, dashed, dotted, dot-dash)
    and visibility.
  - Shading: to another curve, to the track min, to the track max, or a gradient colour ramp
    (Earth, Rainbow, Grayscale, Heat or Viridis) driven by value across the track scale, with fill
    colour and opacity.
  - Bed boundaries: add, edit or delete user boundaries (name, TVT offset, colour, thickness, label),
    and restyle or hide imported beds.
- **Workspace**: **Save workspace** (Ctrl+S) writes `gordon.geosteering.workspace` JSON to
  `%LOCALAPPDATA%\GordonGeoSteer\workspaces\<project>-<hash>.gws.json`. The hash comes from the PZS
  source folder, project and model name, so it is the same across re-imports. Set
  `GORDONGEOSTEER_WORKSPACE_DIR` to put it somewhere else, or use **Steering > Save workspace as /
  Load workspace**. The file is loaded automatically when the project is opened again. PZS files are
  never written.
- **Steering > Run engine validation** shows the validation report for the open project.

### Interpretation decisions (where the spec was ambiguous)

- **TVT** (spec definition, *ParallelToReference* mode): `TVT = TVD - (reference bed grid depth at N/E
  + that bed's geomod dip shift(MD) + fault shift)`, positive down.
  - Marker reference beds get their depth from the depth-index model.
  - Offset logs are placed on the same axis as `offset TVD(md) - offset TVD(reference bed's offset
    MD)`. The offset TVD comes from the offset survey by minimum curvature, including the well
    location and tie-in.
- **DepthIndexStretch mode** (extra; selectable): the lateral sample's depth index, which is the
  correlated offset MD, comes from the PZS depth-index model, which stretches between gridded beds.
  This reproduces column 3 of PZS's `_M` curves (see validation) and correlates better.
  ParallelToReference stays the default because it is the spec's definition.
- **Default reference bed**: the gridded bed nearest the lateral (smallest median |TVT|). For the Bakken
  sample that is *Lower Bakken Top*.
- **User boundaries**: a TVT offset from the reference bed, parallel to it. In the curtain this is
  `reference depth + offset`.
  - Imported beds in the vertical tracks are drawn at their TVT at the offset well.
  - In the curtain, imported beds follow the depth-index model, so their spacing can differ from the
    offset well's where the isopach changes.
- **Geomod**: uses the model's live `steering.points` from `project.json`, never the `.gmp` named by
  `GMPFileName`.
  - Before the first pick the shift is the first pick's value.
  - After the last pick the slope decays over `decayDistance`.
  - A pick's fault throw ramps (Hermite, zero end slopes) to 0 at the next pick, because the next
    pick's bed shifts already contain it. After the last pick the throw is kept constant.
- **Bed dip** for the cos(dip) terms: grid-surface gradient (central differences, h = 8 ft halving),
  plus the geomod slope along the hole azimuth. This follows the well-path convention; PZS's
  general-point argument-swap bug is not reproduced.
- **Offset logs below the last offset survey station** use straight extrapolation of the last station.
  The Bakken pilot survey ends at MD 10068 and its logs go to 10361.
- **Curtain x outside the well's VS span**: points on the section line through the origin, using the
  geomod shifts at the nearest end of the well.
- **Curve-to-curve shading** fills between curve A and curve B evaluated at A's positions, with B
  interpolated along the track axis.

## Not yet built (deliberately)

Real-time (WITSML/OPC) feed, interactive geomod editing (adding or dragging picks), resistivity
forward modeling, projection-to-bit and target planning, LAS import, map view, and editing PZS data.
