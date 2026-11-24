# Adjutant skin contract

A skin is one folder: `skins/<name>/skin.xaml`, plus any local image files it references.

Name the central nucleus ellipse `x:Name="Core"`. The host uses its transformed ellipse bounds for the core-only right-click menu; other right-clicks pass underneath. Without a named core, the skin has no pointer menu target. Keep this name in generator base sources as well as generated skins.

The host (`adjutant.exe`, source `adjutant.cs`) handles the rest:
- It loads the skin with `XamlReader`, scales it to the window with a `Viewbox`, and drives it with named storyboards.
- A saved change to the active `skin.xaml` hot-reloads within about a second, and a load error shows in the Adjutant's bubble.
- To pick a skin, right-click the Adjutant and choose **Skin**.

## Hard rules (the host enforces these and refuses the skin otherwise)
- The root element is a `FrameworkElement` (normally a `Grid`) with a fixed design `Width` and `Height`. Square is best; `256 x 256` is the reference size.
- Only the standard WPF XAML namespaces are allowed. No `x:Class`, `x:Code`, `clr-namespace`, `assembly=`, `ObjectDataProvider`, `XmlDataProvider`, `EventSetter`, `Frame`, `WebBrowser` or `MediaElement`.
- No `://` URIs other than the xmlns declarations. Images must be relative files in the skin folder; they resolve against it.
- The background stays transparent. Fully transparent pixels are click-through, so only visible pixels respond to drag and click. The host adds an invisible centred click disc (`hitZone` in `config.json`, 0.4 of the width), so keep the core of the design inside that circle.

## Storyboards (put them in the root's `Resources`, keyed exactly as below)
| Key | When the host runs it | Required |
|---|---|---|
| `Motion` | Starts at load and loops forever. The host sets its speed ratio per state (`config.json` → `speeds`: Idle 1.0, Busy 2.5, Attention 1.5, Done 1.0, Error 0.6). | Recommended |
| `Idle`, `Busy`, `Attention`, `Done`, `Error` | On entering that state. The previous state's storyboard is stopped first, so animated values fall back to their base (Idle) values. Loop with `RepeatBehavior="Forever"` or hold the end value. | Optional each; a missing one means "base look" |
| `Ping` | One-shot whenever a message arrives, composed on top of the current state. Animate only elements no state storyboard touches, or the two will fight. | Optional |

States mean:
- **Idle**: no agent is working.
- **Busy**: at least one agent is working.
- **Attention**: an agent is blocked and needs attention. This is the highest priority.
- **Done**: an agent finished and its tab has not been visited yet.
- A focused pane finishing a turn also shows Done for 2 seconds, without a bubble. A new focused completion can restart Done even if it is already active; unchanged polls do not replay it. Attention and explicit ping overrides take precedence.
- **Error**: only sent by explicit pings.

Storyboards target elements by `Storyboard.TargetName`. Named brushes, gradient stops, transforms and effects all work as targets, as `skins/atom/skin.xaml` shows.

## Design notes
- The host applies the selected 24/30/60 FPS target to root storyboards. Changing it recreates active Motion/state clocks at their current local time and speed, without reloading the skin or replaying a finished Done ripple. An already-running short Ping/fade finishes at its original cap; subsequent animations use the new selection.
- The Adjutant runs from 24 DIP up to 800 DIP. The presets are Small 48, Medium 96 and Large 192. Make sure the skin still reads at 48, where the 256 design is scaled down about 5x. Thin strokes and small electrons vanish there.
- It sits on top of other windows all day, so the idle state should be calm. Save the strong motion for Attention.
- It can rest at 20% opacity (less-distraction mode), so the state colours must still differ at low opacity.
- The speech bubble belongs to the host, not the skin. Never draw message-like text in the skin; decorative gibberish is fine (see `skins/atom-console`).
- XAML has no random numbers. For "random" effects, bake a long shuffled sequence with a generator script, as `skins/atom-console/make_skin.py` does. Write non-ASCII characters as XML character references (`&#9608;`): `XamlReader.Parse` rejected a raw UTF-8 glyph.
- `DropShadowEffect` and `BlurEffect` are fine. Avoid many stacked effects, because each one is a GPU pass.
