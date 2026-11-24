"""Helpers for generating the atom-console skin."""
import math
from xml.sax.saxutils import quoteattr

# Console skin font (IosevkaTerm Nerd Font Mono); WPF knows it by this family name.
FONT = "IosevkaTerm NFM"


def ts(seconds):
    # TimeSpan text rejects 60 or more in the seconds field.
    ms = int(round(seconds * 1000))
    return "0:{}:{:02d}.{:03d}".format(ms // 60000, ms // 1000 % 60, ms % 1000)


def _value_at(frames, t):
    """Value of a keyframe track at time t: discrete frames hold, linear frames interpolate."""
    prev_t, prev_v = 0.0, None
    for at, kind, value in frames:
        if at > t:
            if kind == "L" and prev_v is not None:
                return prev_v + (value - prev_v) * (t - prev_t) / (at - prev_t)
            return prev_v
        prev_t, prev_v = at, value
    return prev_v


def render(kind, target, prop, frames, cycle):
    """One looping keyframe animation from (time, "D"|"L", value) frames.

    Frames past the cycle end wrap to its start, so the hold and fade of the last block carry on
    over the loop restart instead of the typing pausing until they finish. The track then starts
    at the value it had at the cycle end, which keeps the restart seamless.
    """
    frames = sorted(frames, key=lambda f: f[0])
    wrapped = [(at - cycle, k, v) for at, k, v in frames if at > cycle]
    inside = [f for f in frames if f[0] <= cycle]
    if wrapped:
        start = _value_at(frames, cycle)
        inside = [f for f in inside if f[0] > 0]
        frames = [(0.0, "D", start)] + wrapped + inside
        assert all(a[0] <= b[0] for a, b in zip(frames, frames[1:])), (target, prop, "wrapped frames overlap")
    elif frames and frames[0][0] > 0:
        # No block of this slot starts at 0: hold the end value (e.g. the last block's position)
        # until the slot's first block, or the wrapped fade jumps to the next spot.
        frames = [(0.0, "D", _value_at(frames, cycle))] + inside
    else:
        frames = inside
    name = "String" if kind == "String" else "Double"
    xml = []
    for at, k, v in frames:
        frame = ("Discrete" if k == "D" else "Linear") + name + "KeyFrame"
        xml.append("<{} KeyTime={} Value={}/>".format(frame, quoteattr(ts(at)), quoteattr(str(v))))
    return ('<{0}AnimationUsingKeyFrames Storyboard.TargetName="{1}" Storyboard.TargetProperty="{2}" '
            'Duration="{3}">\n        {4}\n      </{0}AnimationUsingKeyFrames>').format(
                name, target, prop, ts(cycle), "\n        ".join(xml))


def _overlap(a, b, margin=4):
    return (a[0] < b[0] + b[2] + margin and b[0] < a[0] + a[2] + margin
            and a[1] < b[1] + b[3] + margin and b[1] < a[1] + a[3] + margin)


def assign_spots(rng, blocks, positions, size, window, attempts=2000):
    """Give each block a spot that overlaps none of the `window` blocks before it, cyclically.

    Up to window + 1 blocks are on screen at once (one typing, the others holding or fading), and the
    last blocks of the loop stay up while the first ones type. positions(block) lists candidate (x, y);
    size(block) gives (width, height). Deterministic for a given rng state.
    """
    n = len(blocks)
    for _ in range(attempts):
        rects = []
        for block in blocks:
            candidates = list(positions(block))
            rng.shuffle(candidates)
            w, h = size(block)
            recent = rects[-window:]
            pick = next(((x, y) for x, y in candidates
                         if not any(_overlap((x, y, w, h), r) for r in recent)), None)
            if pick is None:
                break
            rects.append((pick[0], pick[1], w, h))
        else:
            if not any(_overlap(rects[i], rects[(i + k) % n])
                       for i in range(n - window, n) for k in range(1, window + 1) if i + k >= n):
                for block, r in zip(blocks, rects):
                    block["spot"] = r[:2]
                return
    raise RuntimeError("no overlap-free spot order found")


ROUNDS = 5   # placement rounds: positions repeat only every ROUNDS typing loops


def place(rng, blocks, candidates, size, slots):
    """Spots for ROUNDS x len(blocks) placements, so the same text lands somewhere else each loop.

    candidates(block, rng) lists (x, y) options, jittered by the caller; size(block) is (width, height).
    Up to len(slots) blocks show at once, so a block may not overlap the len(slots) - 1 before it.
    """
    placements = [dict(block) for _ in range(ROUNDS) for block in blocks]
    jittered = {id(p): candidates(p, rng) for p in placements}
    assign_spots(rng, placements, lambda p: jittered[id(p)], size, len(slots) - 1)
    return [p["spot"] for p in placements]


def staggered_starts(rng, blocks, slots, delay, hold, fade):
    """Block start times when each block starts a random `delay` after the previous one started.

    Blocks may type at the same time, up to one per slot. Block i uses slot i % len(slots); when that
    slot still shows its previous block, the new block waits until it has faded. Returns the starts and
    the loop length, which also keeps the slots free over the loop restart.
    """
    n, k = len(blocks), len(slots)
    assert n % k == 0, "blocks must be a multiple of the slot count, so each block keeps its slot"
    shown = [b["typed_end"] + hold + fade for b in blocks]
    starts = [0.0]
    for i in range(1, n):
        t = starts[-1] + rng.uniform(*delay)
        if i >= k:
            t = max(t, starts[i - k] + shown[i - k])
        starts.append(t)
    cycle = starts[-1] + rng.uniform(*delay)
    for j in range(n - k, n):                       # the next loop's first blocks reuse these slots
        cycle = max(cycle, starts[j] + shown[j] - starts[j + k - n])
    # Whole 2 ms steps keep cycle / speed exact in ms, so the position loop never drifts from the text.
    return starts, math.ceil(cycle / 0.002) * 0.002


def place_timed(rng, blocks, starts, cycle, candidates, size, hold, fade, attempts=2000):
    """Spots for ROUNDS x len(blocks) placements, none overlapping another block shown at the same time.

    Placement r * n + i shows block i from r * cycle + starts[i] until it has faded; the ROUNDS loops
    repeat, so the last placements also avoid the first ones. candidates(block, rng) lists jittered
    (x, y) options; size(block) is (width, height). Deterministic for a given rng state.
    """
    n = len(blocks)
    period = ROUNDS * cycle
    spans = []
    for p in range(ROUNDS * n):
        r, i = divmod(p, n)
        a = r * cycle + starts[i]
        spans.append((a, a + blocks[i]["typed_end"] + hold + fade))
    together = [[q for q in range(p) if any(spans[q][0] + m * period < spans[p][1]
                                            and spans[p][0] < spans[q][1] + m * period for m in (-1, 0, 1))]
                for p in range(len(spans))]
    options = [candidates(blocks[p % n], rng) for p in range(len(spans))]
    for _ in range(attempts):
        rects = []
        for p, cands in enumerate(options):
            w, h = size(blocks[p % n])
            cands = cands[:]
            rng.shuffle(cands)
            pick = next(((x, y) for x, y in cands
                         if not any(_overlap((x, y, w, h), rects[q]) for q in together[p])), None)
            if pick is None:
                break
            rects.append((pick[0], pick[1], w, h))
        else:
            return [r[:2] for r in rects]
    raise RuntimeError("no overlap-free placement found")


def typing_xml(blocks, spots, slots, speed, hold, fade, blink, cursor, starts=None, cycle=None):
    """Two looping ParallelTimelines for the typing overlay, plus the loop length in seconds.

    Text and opacity loop every cycle; positions loop every ROUNDS cycles. Without `starts`, the next
    block starts in the next slot as soon as the current one finishes typing, so typing never pauses,
    and the loop restarts when the last block finishes typing. With `starts` and `cycle` (see
    staggered_starts()), blocks start at those times and may type at once. Either way, frames that run
    past the loop end wrap over the restart (see render()).
    """
    n, k = len(blocks), len(slots)
    assert n % k == 0, "blocks must be a multiple of the slot count, so each block keeps its slot"
    if starts is None:
        starts = []
        for i, b in enumerate(blocks):
            starts.append(starts[-1] + blocks[i - 1]["typed_end"] if i else 0.0)
        # Whole 2 ms steps keep cycle / speed exact in ms, so the position loop never drifts from the text.
        cycle = round((starts[-1] + blocks[-1]["typed_end"]) / 0.002) * 0.002
    for i in range(k, n + k):                       # a slot is free again before its next block starts
        prev, start = i - k, (starts[i] if i < n else cycle + starts[i - n])
        assert starts[prev] + blocks[prev]["typed_end"] + hold + fade <= start + 1e-9, "slot still busy"
    c = cycle / speed
    typing, positions = [], []
    for slot in range(k):
        name = "Note" + slots[slot]
        text, opacity, left, top = [(0.0, "D", "")], [(0.0, "D", 0)], [], []
        for i in range(slot, n, k):
            block, start = blocks[i], starts[i]
            s = lambda x: (start + x) / speed
            opacity.append((s(0), "D", 1))
            for at, value in block["steps"]:
                text.append((s(at), "D", value))
            end = block["typed_end"]
            t, on = end, False
            while t < end + hold:
                t += blink
                text.append((s(t), "D", block["text"] + (cursor if on else "")))
                on = not on
            opacity.append((s(end + hold), "D", 1))
            opacity.append((s(end + hold + fade), "L", 0))
            text.append((s(end + hold + fade), "D", ""))
            for r in range(ROUNDS):
                x, y = spots[r * n + i]
                at = (r * cycle + start) / speed
                left.append((at, "D", round(x)))
                top.append((at, "D", round(y)))
        typing += [render("String", name, "Text", text, c), render("Double", name, "Opacity", opacity, c)]
        positions += [render("Double", name, "(Canvas.Left)", left, c * ROUNDS),
                      render("Double", name, "(Canvas.Top)", top, c * ROUNDS)]

    def group(parts, duration):
        return ('\n      <ParallelTimeline RepeatBehavior="Forever" Duration="{}">\n      {}\n      </ParallelTimeline>'
                .format(ts(duration), "\n      ".join(parts)))

    return group(typing, c) + group(positions, c * ROUNDS) + "\n    ", c
