# SMITE 1 Inspector

A native Windows desktop app (C# / WinForms, .NET 8) for **SMITE 1** with two tools:

- **God Inspector** — browse and edit the per-god ability tuning values that SMITE 1
  exposes in its `Battle<God>.ini` config files (for solo / jungle-practice tinkering).
- **Player Tracker** — look up any SMITE 1 player via the official Hi-Rez API: profile,
  god masteries, recent matches + scoreboards, achievements, and friends. Includes a
  curated **Friend List** with live online/in-game status, and a best-effort heuristic
  for re-recognising privacy-flagged players you've manually nicknamed.

> SMITE 1 is in maintenance mode, but its servers and stats API are still up. This is an
> unofficial fan tool — see **Disclaimer** below.

## Build

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). In the repo
folder:

```
dotnet run                                  # run while developing
```

For a single portable, self-contained exe (no .NET install needed to run it):

```
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The result is `bin\Release\net8.0-windows\win-x64\publish\SmiteGodLab.exe` (~158 MB).
`build.bat` runs the same publish.

## API key (Player Tracker)

The tracker calls the official Hi-Rez SMITE 1 API, which needs a developer `devId` +
`authKey`. The app ships with a working default key so it runs out of the box, but it is
**rate-limited and shared** — please use your **own** free key for anything serious.

Request one from Hi-Rez, then create a file named **`api.txt`** containing one line:

```
yourDevId,yourAuthKey
```

Put it next to the exe, or in `Documents\Smite Inspector\`. The app prefers `api.txt`
over the built-in default. (`api.txt` is git-ignored.)

## Using it

- **God Inspector** auto-detects `Documents\My Games\Smite\BattleGame\Config`. Pick a god,
  edit a value, **Apply** — a timestamped `.bak` is saved next to the file every time.
  `CLIENT`-scoped values are the ones most likely to take effect in a local solo match.
- **Player Tracker** — type a SMITE name (partial / any case works) and **Search**. Save
  players to **Favorites**, add them to your **Friend List** (live status), or open a match
  for the full scoreboard.

User data (favorites, friend list, recents, settings, nicknames) lives in
`Documents\Smite Inspector\` as JSON.

## Disclaimer

This is an **unofficial, fan-made** tool. It is not affiliated with, endorsed by, or
sponsored by Hi-Rez Studios / Titan Forge Games. **SMITE**, its god/item artwork, and
related marks are property of Hi-Rez Studios; platform logos (Steam, Xbox, Epic, Nintendo
Switch) belong to their respective owners. They are included here solely to identify the
in-game content the tool displays.

## License

[GPL-3.0](LICENSE).
