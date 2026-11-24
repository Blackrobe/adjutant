"""Memory-dump bursts for the atom-console skin.

Now and then, while Busy or Attention, the typing overlay fades out and a wide band of synthetic
hexdump lines scrolls over the atom for 20 to 60 seconds. No real memory is read.

The bursts run on their own loop, whose length differs from the typing cycle, so the two patterns
line up the same way only after many minutes: cheap pseudo-randomness, since XAML has none.
The scroll is a single TranslateTransform over a fixed, pre-baked column of lines, so it costs one
moving layer whatever the speed, and the transform only changes while a burst is showing.
"""
from xml.sax.saxutils import quoteattr

from console_common import FONT

BURSTS = 12                 # bursts per loop: the set repeats only after about 10 minutes
QUIET = (20.0, 60.0)        # typing time between bursts (Busy speed; Attention runs twice as fast)
FIRST = (20.0, 30.0)        # wait before the first burst after entering the state (the loop restart reuses it)
LENGTH = (20.0, 60.0)       # burst length, uniformly random per burst
FADE = 0.2                  # cross-fade between typing and dump
LINES = 40                  # unique dump lines in the scrolling column
LINES_PER_SECOND = 2
FONT_SIZE = 9
LINE_HEIGHT = 11
BAND = (8, 70, 240, 116)    # left, top, width, height in the 256 design: a wide band across the atom
SEED_OFFSET = 1000          # the dump uses its own random stream, so the typing stays as it was


def _line(rng, offset):
    if rng.random() < 0.25:                                 # zero padding, as real dumps have
        data = [0] * 8
    elif rng.random() < 0.3:                                # a run of printable text
        data = [rng.randrange(0x41, 0x7B) for _ in range(8)]
    else:
        data = [rng.randrange(256) for _ in range(8)]
    hexes = " ".join("{:02X}".format(b) for b in data)
    text = "".join(chr(b) if 0x20 < b < 0x7F else "." for b in data)
    return "{:08X}  {}  |{}|".format(offset, hexes, text)


def text(rng):
    """The scrolling column: LINES unique lines, then enough of them again for a seamless wrap."""
    base = rng.randrange(0x00400000, 0x7FF00000) & ~0xF
    lines = [_line(rng, base + 8 * i) for i in range(LINES)]
    visible = BAND[3] // LINE_HEIGHT + 2
    return "\n".join(lines + lines[:visible])


def element(rng, ink):
    left, top, width, height = BAND
    return (
        '    <!-- Memory-dump burst; invisible outside bursts (see ../dump_burst.py). -->\n'
        '    <Canvas x:Name="Dump" Canvas.Left="{0}" Canvas.Top="{1}" Width="{2}" Height="{3}" ClipToBounds="True" '
        'Opacity="0" IsHitTestVisible="False">\n'
        # Absolute mapping: a relative mask follows the scrolling text's bounds, so the fades slid and jumped.
        '      <Canvas.OpacityMask>\n'
        '        <LinearGradientBrush MappingMode="Absolute" StartPoint="0,0" EndPoint="0,{3}">\n'
        '          <GradientStop Offset="0" Color="#00000000"/><GradientStop Offset="0.2" Color="#FF000000"/>\n'
        '          <GradientStop Offset="0.8" Color="#FF000000"/><GradientStop Offset="1" Color="#00000000"/>\n'
        '        </LinearGradientBrush>\n'
        '      </Canvas.OpacityMask>\n'
        '      <TextBlock Width="{2}" TextAlignment="Center" FontFamily="{8}" FontWeight="Medium" FontSize="{4}" '
        'LineHeight="{5}" LineStackingStrategy="BlockLineHeight" '
        'Text={6}>\n'
        '        <TextBlock.Foreground><SolidColorBrush x:Name="InkDump" Color="{7}"/></TextBlock.Foreground>\n'
        '        <TextBlock.RenderTransform><TranslateTransform x:Name="DumpScroll"/></TextBlock.RenderTransform>\n'
        '      </TextBlock>\n'
        '    </Canvas>'
    ).format(left, top, width, height, FONT_SIZE, LINE_HEIGHT, quoteattr(text(rng)), ink, FONT)


def schedule(rng):
    """[(start, end)] of each burst in seconds at Busy speed, and the loop length."""
    bursts, t = [], 0.0
    for i in range(BURSTS):
        t += rng.uniform(*(FIRST if i == 0 else QUIET))
        length = rng.uniform(*LENGTH)
        bursts.append((t, t + length))
        t += length
    return bursts, t


def timelines(bursts, loop, speed, ts):
    """A looping ParallelTimeline: typing layer and dump cross-fade, and the scroll during each burst."""
    s = lambda x: x / speed
    typing = ['<DiscreteDoubleKeyFrame KeyTime="0:0:0" Value="1"/>']
    dump = ['<DiscreteDoubleKeyFrame KeyTime="0:0:0" Value="0"/>']
    scroll = ['<DiscreteDoubleKeyFrame KeyTime="0:0:0" Value="0"/>']
    wrap = LINES / float(LINES_PER_SECOND)
    distance = -LINES * LINE_HEIGHT
    for start, end in bursts:
        for frames, a, b in ((typing, 1, 0), (dump, 0, 1)):
            frames.append('<DiscreteDoubleKeyFrame KeyTime="{}" Value="{}"/>'.format(ts(s(start)), a))
            frames.append('<LinearDoubleKeyFrame KeyTime="{}" Value="{}"/>'.format(ts(s(start + FADE)), b))
            frames.append('<DiscreteDoubleKeyFrame KeyTime="{}" Value="{}"/>'.format(ts(s(end - FADE)), b))
            frames.append('<LinearDoubleKeyFrame KeyTime="{}" Value="{}"/>'.format(ts(s(end)), a))
        t = start
        scroll.append('<DiscreteDoubleKeyFrame KeyTime="{}" Value="0"/>'.format(ts(s(t))))
        while t + wrap < end:                               # full passes, each jumping back to the top
            t += wrap
            scroll.append('<LinearDoubleKeyFrame KeyTime="{}" Value="{}"/>'.format(ts(s(t)), distance))
            scroll.append('<DiscreteDoubleKeyFrame KeyTime="{}" Value="0"/>'.format(ts(s(t) + 0.001)))
        scroll.append('<LinearDoubleKeyFrame KeyTime="{}" Value="{:.1f}"/>'.format(
            ts(s(end)), distance * (end - t) / wrap))

    def anim(target, prop, frames):
        return ('<DoubleAnimationUsingKeyFrames Storyboard.TargetName="{}" Storyboard.TargetProperty="{}" '
                'Duration="{}">\n        {}\n      </DoubleAnimationUsingKeyFrames>').format(
                    target, prop, ts(s(loop)), "\n        ".join(frames))

    parts = [anim("TypingLayer", "Opacity", typing), anim("Dump", "Opacity", dump), anim("DumpScroll", "Y", scroll)]
    return ('\n      <ParallelTimeline RepeatBehavior="Forever" Duration="{}">\n      {}\n      </ParallelTimeline>'
            .format(ts(s(loop)), "\n      ".join(parts)))


def ink_animation(color):
    return ('      <ColorAnimation Storyboard.TargetName="InkDump" Storyboard.TargetProperty="Color" '
            'To="{}" Duration="0:0:0.2"/>'.format(color))
