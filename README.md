# Pete

The Unity 2021.3.12f1 project for **Pete**, plus a TAS (tool-assisted speedrun) tool built on top of it.

## Download and play the TAS (Windows)

You don't need Unity for this. The green **Code → Download ZIP** button only gives you the source code, not the program.

1. Go to the **[latest release](https://github.com/PeteHistorian/pete-tas/releases/latest)** and download **`Pete-TAS-...-Windows.zip`** (under "Assets").
2. Extract (unzip) the whole zip, for example to your Desktop. Don't run it from inside the zip.
3. Open the `Pete TAS` folder and double-click **`Pete TAS.exe`**.
4. If Windows says "Windows protected your PC", click **More info**, then **Run anyway**. The program isn't signed, which is normal for small tools.

Your movies, presets and videos are saved in the `TAS Files` folder next to `Pete TAS.exe`. Press **F1** in the TAS for help, or read [HOW TO USE THE TAS.md](HOW%20TO%20USE%20THE%20TAS.md).

## The TAS tool (for developers)

- Code: `Pete/Assets/TAS/`, with small hooks in the game scripts (`PeteInput`, `TasHooks`). Normal play is unchanged.
- In Unity: open `Pete/` and use the menu **Pete TAS → Play TAS**. **Pete TAS → Build Pete TAS.exe** makes the standalone tool.
- Self-test: **Pete TAS → Run Self-Test**, or run `"Pete TAS.exe" -tasSelfTest -batchmode -nographics`.
