# Mycopunk Build Architect — prototype 0.1.0

A BepInEx 5 plugin prototype for Mycopunk. It adds a small in-game Build Architect window to the Gear Details screen and stores named build presets locally.

## Important compatibility notes

- The plugin is designed against the uploaded Mycopunk Unity/Mono assemblies and the `GridSolverCoru 1.1.2` API shape.
- Install **GridSolverCoru 1.1.2** and **SparrohUILib 1.2.2** before using this plugin.
- Do **not** run this alongside the old `UpgradeSolver 0.1.2` plugin. GridSolverCoru is a fork/replacement solver and both plugins can register overlapping UI and input hooks.
- This is an early integration prototype. The current profile format saves the selected upgrade IDs from GridSolverCoru, not a pixel-perfect/manual cell layout. Applying a profile asks GridSolverCoru to calculate a valid arrangement for the current grid.
- It has not been compiled or tested in-game in this environment. Build against the exact game installation and test with a backed-up profile.

## Build

Install Visual Studio 2022 with the .NET desktop development workload, or another MSBuild environment with .NET Framework 4.8 targeting pack.

If Mycopunk is installed in the default Steam path:

```powershell
msbuild .\BuildArchitect.csproj /p:Configuration=Release
```

For a non-default install:

```powershell
msbuild .\BuildArchitect.csproj /p:Configuration=Release /p:GameDir="D:\SteamLibrary\steamapps\common\Mycopunk"
```

The DLL will be in `bin\Release\net48\MycopunkBuildArchitect.dll`.

To build and copy directly into BepInEx:

```powershell
msbuild .\BuildArchitect.csproj /p:Configuration=Release /p:Deploy=true
```

## Install

Copy `MycopunkBuildArchitect.dll` into:

`Mycopunk\BepInEx\plugins\MycopunkBuildArchitect\`

The profile database is stored at:

`BepInEx\config\MycopunkBuildArchitect\profiles.json`

## Use

1. Install GridSolverCoru and SparrohUILib.
2. Disable/remove UpgradeSolver 0.1.2 to avoid duplicate solver UI.
3. Launch the game and open a gear upgrade details window.
4. Hover an unlocked upgrade and press `N` to select it using GridSolverCoru.
5. Press **F7** to open/close Build Architect.
6. Name the preset and click **Save selected upgrades**.
7. Select **Apply / Solve** to re-select the saved upgrades and pass them to GridSolverCoru.

The solver remains responsible for rotation, grid expansion, fit checking, and actual placement.
