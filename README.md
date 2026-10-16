# Pete

The Unity 2021.3.12f1 project for **Pete**, plus a TAS (tool-assisted speedrun) tool built on top of it.

## The TAS tool

- Code: `Pete/Assets/TAS/`, with small hooks in the game scripts (`PeteInput`, `TasHooks`). Normal play is unchanged.
- In Unity: open `Pete/` and use the menu **Pete TAS → Play TAS**. **Pete TAS → Build Pete TAS.exe** makes the standalone tool.
- How to use it: see [HOW TO USE THE TAS.md](HOW%20TO%20USE%20THE%20TAS.md).
- Self-test: **Pete TAS → Run Self-Test**, or run `"Pete TAS.exe" -tasSelfTest -batchmode -nographics`.
