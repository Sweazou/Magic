# <img width="32" height="32" alt="icon" src="https://github.com/user-attachments/assets/43cefb61-0e92-4224-8ab2-238f2d310bf8" /> Magic
**M**agic is an **A**utomatic **G**ames **I**mporter **C**ompanion for [Playnite](https://github.com/JosefNemec/Playnite).

It **“magically”** detect all your games in a given folder(s) and add them all to playnite automatically.

## How It Works

The plugin handles game detection itself using a scoring-based system to determine the most likely `.exe` file for each game.

For each game folder, it:

1. Scans all `.exe` files recursively.
2. Marks obvious utility or non-game executables.
3. Uses only non-bad candidates for primary scoring when possible.
4. Sorts candidates by highest score.
5. Uses file size as a tiebreaker when scores are equal.
6. Selects the best candidate if its score is at least `30`.
7. Falls back to the largest `.exe` in the folder if the best score is below `30`.

## Scoring System

Each `.exe` receives points based on how likely it is to be the main game executable. The candidate with the highest score is selected.

### Bonuses

- Exact main folder name match: `+80`
- Main folder name is contained in the executable name: `+60`
- Cleaned game name is contained in the executable name: `+40`
- Exact alternate parent-folder match: `+50`
- Alternate parent-folder name is contained in the executable name: `+30`
- Same first letter as the main, cleaned, or parent candidate name: `+35`
- Path contains `Binaries`, `Win64`, or `x64`: `+50`
- File size is at least `50 MB`: `+40`
- File size is at least `150 MB`: `+60`

### Penalties

- Obvious utility, crash, setup, launcher, or updater executables: `-200`
