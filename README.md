# Adjutant

Adjutant is a small, always-on-top Windows companion for [herdr](https://herdr.dev/).
Its animated skins show when an agent is busy, blocked, or done without making
you switch terminals.

## Build and run

1. On 64-bit Windows, use the .NET Framework 4.8 tools and put `herdr.exe` on your PATH.
2. From the repository root, run `.\build.cmd` in PowerShell.
3. Launch `adjutant.exe` from this directory. Keep `config.json` and the `skins` directory beside it.

Drag Adjutant to move it. Right-click the center of the atom for skin, size,
frame-rate, reset, and quit options. A first launch or **Reset position** places
it at the center-right of the primary screen. The optional Hacker News ticker
reads headlines from the public front-page RSS feed; it is off by default.

Left-clicking focuses a herdr tab and brings Windows Terminal forward. The app
uses a Windows low-level mouse hook to pass right-clicks outside the core
through to the window underneath. It stores settings and a log in
`%APPDATA%\adjutant`.

## Local pings

With Python 3 installed, run this from the repository root:

```powershell
py -3 -I adjutant_ping.py --from demo --msg "Build finished" --state done
```

The ping listener is bound to loopback. Other programs on the same machine can
send pings; do not expose it as a network service. Console skins show generated
hex text, not process memory.

## Skins

See [the skin contract](SKIN-CONTRACT.md) to make a skin. Load only XAML skins
you trust. The console skin is designed for IosevkaTerm Nerd Font Mono;
it uses a fallback if the font is not installed. To regenerate it, run:

```powershell
py -3 -I skins/atom-console/make_skin.py
```
