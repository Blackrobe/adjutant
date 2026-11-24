// Adjutant: an always-on-top desktop companion for herdr agents (see README.md and SKIN-CONTRACT.md).
//
// - Animates a XAML skin (skins/<name>/skin.xaml) by agent state.
// - State comes from `herdr agent list` (polled) plus pings on http://127.0.0.1:<port>/ping.
// - Left click: focus the herdr tab that needs attention, then bring Windows Terminal forward.
// - Drag to move; corner grip or Ctrl+wheel to resize; right click for the menu.
// - Ctrl+click: a minigame (ICE breaker or NET SWEEP, picked in the menu).
//
// Build with build.cmd: C# 5 via the .NET Framework 4.8 csc.exe that ships with Windows (no SDK).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;

namespace Adjutant
{
    static class Win32
    {
        [StructLayout(LayoutKind.Sequential)] public struct ScreenPoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct WindowRect { public int Left, Top, Right, Bottom; }
        public delegate IntPtr MouseHookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int kind, MouseHookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string module);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(ScreenPoint point);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out WindowRect rect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLong64(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong32(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLong64(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong32(IntPtr window, int index, int value);
        public static void SetMouseTransparent(IntPtr window, bool transparent)
        {
            const int extendedStyle = -20;
            var style = IntPtr.Size == 8 ? GetWindowLong64(window, extendedStyle).ToInt64() : GetWindowLong32(window, extendedStyle);
            var updated = transparent ? style | 0x20L : style & ~0x20L;
            if (style == updated) return;
            if (IntPtr.Size == 8) SetWindowLong64(window, extendedStyle, new IntPtr(updated));
            else SetWindowLong32(window, extendedStyle, (int)updated);
        }
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);

        // WPF's Keyboard.Modifiers is stale while the Adjutant is not the active window; this reads the key itself.
        public static bool CtrlDown() { return (GetAsyncKeyState(0x11) & 0x8000) != 0; }
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        public delegate bool EnumProc(IntPtr h, IntPtr p);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

        public static List<IntPtr> TerminalWindows()
        {
            var list = new List<IntPtr>();
            EnumWindows((h, p) =>
            {
                var c = new StringBuilder(256);
                GetClassName(h, c, 256);
                if (c.ToString() == "CASCADIA_HOSTING_WINDOW_CLASS" && IsWindowVisible(h)) list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

        // True while a Windows Terminal window (where herdr runs) is the foreground window.
        public static bool TerminalInFront()
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return false;
            var c = new StringBuilder(256);
            GetClassName(h, c, 256);
            return c.ToString() == "CASCADIA_HOSTING_WINDOW_CLASS";
        }

        public static string Title(IntPtr h)
        {
            var s = new StringBuilder(512);
            GetWindowText(h, s, 512);
            return s.ToString();
        }
    }

    static class Json
    {
        public static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static Dictionary<string, object> Object(object o) { return o as Dictionary<string, object>; }

        public static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(Dictionary<string, object> d, string key) { return Get(d, key) as string; }

        public static double Num(Dictionary<string, object> d, string key, double fallback)
        {
            var v = Get(d, key);
            return v == null ? fallback : Convert.ToDouble(v);
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool fallback)
        {
            var v = Get(d, key);
            return v is bool ? (bool)v : fallback;
        }
    }

    class Config
    {
        public int Port;
        public string Skin;
        public double Size;
        public int Fps;
        public double DimOpacity;
        public int PollSeconds;
        public int BubbleSeconds;
        public int OverrideSeconds;
        public double HitZone;
        public string TerminalTitleRegex;
        public Dictionary<string, double> Speeds = new Dictionary<string, double>();

        public static Config Load(string path)
        {
            var d = Json.Object(Json.Serializer.DeserializeObject(File.ReadAllText(path)));
            var c = new Config
            {
                Port = (int)Json.Num(d, "port", 47777),
                Skin = Json.Str(d, "skin") ?? "atom",
                Size = Json.Num(d, "size", 160),
                Fps = (int)Json.Num(d, "fps", 24),
                DimOpacity = Json.Num(d, "dimOpacity", 0.2),
                PollSeconds = (int)Json.Num(d, "pollSeconds", 2),
                BubbleSeconds = (int)Json.Num(d, "bubbleSeconds", 8),
                OverrideSeconds = (int)Json.Num(d, "overrideSeconds", 8),
                HitZone = Math.Min(1.0, Math.Max(0.0, Json.Num(d, "hitZone", 0.4))),
                TerminalTitleRegex = Json.Str(d, "terminalTitleRegex") ?? ".",
            };
            var speeds = Json.Object(Json.Get(d, "speeds"));
            foreach (var s in new[] { "Idle", "Busy", "Attention", "Done", "Error" })
                c.Speeds[s] = Json.Num(speeds, s, 1.0);
            return c;
        }
    }

    // Persisted per-user window state (%APPDATA%\adjutant\state.json).
    class Saved
    {
        public double? Left, Top;
        public double Size;
        public string Skin;
        public bool Quiet, Grip = true, Dim, HnNews;
        public string NewsSide = "Top";
        public int BubbleSeconds;                 // 0 = config.json bubbleSeconds ("Standard"), -1 = until herdr is opened
        public int Fps;
        public string Minigame;                   // "ice" (ICE breaker) or "sweep" (NET SWEEP)
        public int HackRuns, HackWins, HackStreak, HackBest;       // ICE breaker stats
        public int SweepRuns, SweepWins, SweepStreak, SweepBest;   // NET SWEEP stats

        public static int FrameRateOrDefault(int fps) { return fps == 24 || fps == 30 || fps == 60 ? fps : 24; }
        public static string NewsSideOrDefault(string side)
        { return side == "Top" || side == "Left" || side == "Right" || side == "Bottom" ? side : "Top"; }

        public static Saved Load(string path, Config cfg)
        {
            var s = new Saved { Size = cfg.Size, Skin = cfg.Skin, Fps = FrameRateOrDefault(cfg.Fps) };
            if (!File.Exists(path)) return s;
            var d = Json.Object(Json.Serializer.DeserializeObject(File.ReadAllText(path)));
            if (Json.Get(d, "left") != null) s.Left = Json.Num(d, "left", 0);
            if (Json.Get(d, "top") != null) s.Top = Json.Num(d, "top", 0);
            s.Size = Json.Num(d, "size", cfg.Size);
            s.Skin = Json.Str(d, "skin") ?? cfg.Skin;
            s.Quiet = Json.Bool(d, "quiet", false);
            s.Grip = Json.Bool(d, "grip", true);
            s.Dim = Json.Bool(d, "dim", false);
            s.HnNews = Json.Bool(d, "hnNews", false);
            s.NewsSide = NewsSideOrDefault(Json.Str(d, "newsSide"));
            s.BubbleSeconds = (int)Json.Num(d, "bubbleSeconds", 0);
            s.Fps = FrameRateOrDefault((int)Json.Num(d, "fps", s.Fps));
            s.HackRuns = (int)Json.Num(d, "hackRuns", 0);
            s.HackWins = (int)Json.Num(d, "hackWins", 0);
            s.HackStreak = (int)Json.Num(d, "hackStreak", 0);
            s.HackBest = (int)Json.Num(d, "hackBest", 0);
            s.Minigame = Json.Str(d, "minigame") == "sweep" ? "sweep" : "ice";
            s.SweepRuns = (int)Json.Num(d, "sweepRuns", 0);
            s.SweepWins = (int)Json.Num(d, "sweepWins", 0);
            s.SweepStreak = (int)Json.Num(d, "sweepStreak", 0);
            s.SweepBest = (int)Json.Num(d, "sweepBest", 0);
            return s;
        }

        public void Save(string path)
        {
            var d = new Dictionary<string, object>
            {
                { "left", Left }, { "top", Top }, { "size", Size }, { "skin", Skin },
                { "quiet", Quiet }, { "grip", Grip }, { "dim", Dim }, { "bubbleSeconds", BubbleSeconds },
                { "hnNews", HnNews },
                { "newsSide", NewsSide },
                { "fps", Fps }, { "hackRuns", HackRuns }, { "hackWins", HackWins },
                { "hackStreak", HackStreak }, { "hackBest", HackBest }, { "minigame", Minigame },
                { "sweepRuns", SweepRuns }, { "sweepWins", SweepWins }, { "sweepStreak", SweepStreak }, { "sweepBest", SweepBest },
            };
            File.WriteAllText(path, Json.Serializer.Serialize(d), new UTF8Encoding(false));
        }
    }

    class InboxItem
    {
        public string Kind;                       // agents | ping | fault
        public object[] Agents;                   // agents: herdr agent objects, null on error
        public string Error, From, Msg, State, Tab;
        public HnStory[] Stories;
        public int Generation;
    }

    // Public metadata only: no article fetches, browser engine, API key or background process.
    class HnStory
    {
        public int Id, Score, Comments;
        public string Title, Domain;

        public static HnStory Parse(string json, int expectedId)
        {
            var d = Json.Object(new JavaScriptSerializer().DeserializeObject(json));
            if (d == null || Json.Bool(d, "dead", false) || Json.Bool(d, "deleted", false)
                || Json.Str(d, "type") != "story" || Json.Num(d, "id", 0) != expectedId || expectedId <= 0) return null;
            var title = Json.Str(d, "title");
            if (string.IsNullOrWhiteSpace(title)) return null;
            title = WebUtility.HtmlDecode(Regex.Replace(title, "<[^>]*>", ""));
            title = Regex.Replace(Regex.Replace(title, @"[\p{Cc}\p{Cf}]", " "), @"\s+", " ").Trim();
            if (title.Length == 0) return null;
            // Keep titles plain text and bounded, even if an upstream record is malformed.
            var elements = System.Globalization.StringInfo.ParseCombiningCharacters(title);
            if (elements.Length > 180) title = title.Substring(0, elements[180]) + "…";
            Uri url;
            var domain = Uri.TryCreate(Json.Str(d, "url"), UriKind.Absolute, out url)
                && (url.Scheme == "http" || url.Scheme == "https") ? url.Host : "news.ycombinator.com";
            return new HnStory { Id = expectedId, Title = title, Domain = domain,
                Score = Math.Max(0, (int)Json.Num(d, "score", 0)), Comments = Math.Max(0, (int)Json.Num(d, "descendants", 0)) };
        }
    }

    class HnFeed
    {
        public HnStory[] Fetch(CancellationToken cancel)
        {
            // One front-page feed response replaces per-story round trips and warms the whole pool.
            // The framework compiler's legacy target otherwise negotiates obsolete TLS defaults.
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create("https://news.ycombinator.com/rss");
            request.Timeout = request.ReadWriteTimeout = 3500;
            request.UserAgent = "Adjutant-HN-Newswire/1.0";
            request.AllowAutoRedirect = false;
            using (cancel.Register(() => request.Abort()))
            using (var response = request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var bytes = new MemoryStream())
            {
                var buffer = new byte[4096];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (bytes.Length + n > 262144) throw new InvalidDataException("HN response too large");
                    bytes.Write(buffer, 0, n);
                }
                cancel.ThrowIfCancellationRequested();
                return Parse(Encoding.UTF8.GetString(bytes.ToArray()));
            }
        }

        public static HnStory[] Parse(string xml)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 262144 };
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml), settings)) document.Load(reader);
            var stories = new List<HnStory>();
            var ids = new HashSet<int>();
            var serializer = new JavaScriptSerializer();
            foreach (XmlNode item in document.SelectNodes("/rss/channel/item"))
            {
                var title = item.SelectSingleNode("title");
                var comments = item.SelectSingleNode("comments");
                Uri discussion;
                int id;
                if (title == null || comments == null || !Uri.TryCreate(comments.InnerText, UriKind.Absolute, out discussion)
                    || discussion.Scheme != "https" || discussion.Host != "news.ycombinator.com" || discussion.AbsolutePath != "/item"
                    || !int.TryParse(Regex.Match(discussion.Query, @"(?:^\?|&)id=(\d+)(?:&|$)").Groups[1].Value, out id)
                    || id <= 0 || ids.Contains(id)) continue;
                // Reuse the plain-text sanitizer; RSS descriptions and article bodies are ignored.
                var story = HnStory.Parse(serializer.Serialize(new { id = id, type = "story", title = title.InnerText }), id);
                if (story == null) continue;
                stories.Add(story);
                ids.Add(id);
                if (stories.Count == 30) break;
            }
            if (stories.Count == 0) throw new InvalidDataException("HN feed contains no usable headlines");
            return stories.ToArray();
        }
    }

    // A separate ambient channel: never impersonates a ping or changes the atom's state/opacity.
    class HnTicker
    {
        readonly Popup popup;
        readonly Border card;
        readonly TextBlock title, source, previous, older;
        readonly DispatcherTimer timer;
        HnStory story;
        int[] characters;
        readonly Stopwatch clock = new Stopwatch();
        readonly List<HnStory> pool = new List<HnStory>();
        int index = -1;
        string side = "Top";
        public int Count { get { return pool.Count; } }
        public bool IsOpen { get { return popup.IsOpen; } }
        public bool IsMouseOver { get { return card.IsMouseOver; } }

        public HnTicker(FrameworkElement anchor)
        {
            source = new TextBlock { Text = "HN", FontSize = 9, Foreground = Ink("#FFB875"), Margin = new Thickness(0, 0, 0, 2) };
            older = new TextBlock { FontSize = 11, Foreground = Ink("#F2EEE8"), TextWrapping = TextWrapping.Wrap,
                LineHeight = 14, MaxWidth = 200 };
            previous = new TextBlock { FontSize = 11, Foreground = Ink("#F2EEE8"), TextWrapping = TextWrapping.Wrap,
                LineHeight = 14, MaxWidth = 200 };
            title = new TextBlock { FontSize = 11, Foreground = Ink("#F2EEE8"), TextWrapping = TextWrapping.Wrap,
                LineHeight = 14, MaxWidth = 200 };
            var text = new StackPanel { Margin = new Thickness(7, 5, 7, 5),
                Background = null };
            text.Children.Add(source); text.Children.Add(older); text.Children.Add(previous); text.Children.Add(title);
            card = new Border { Width = 216, Background = Ink("#F511151B"), BorderBrush = Ink("#FF8534"),
                BorderThickness = new Thickness(1, 0, 0, 0), CornerRadius = new CornerRadius(2),
                Child = text, Cursor = Cursors.Arrow, Focusable = false };
            TextElement.SetFontFamily(card, new FontFamily("IosevkaTerm NFM"));
            popup = new Popup { AllowsTransparency = true, PlacementTarget = anchor, Placement = PlacementMode.Custom,
                StaysOpen = true, Focusable = false, Child = card };
            // WPF selects the most visible candidate on the target monitor, then adjusts at screen edges.
            popup.CustomPopupPlacementCallback = (size, target, offset) => Placements(size, target, side);
            var owner = Window.GetWindow(anchor);
            if (owner != null)
            {
                owner.LocationChanged += (s, e) => Reposition();
                owner.SizeChanged += (s, e) => Reposition();
            }
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (s, e) => Animate();
        }

        static SolidColorBrush Ink(string color) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); }

        public static CustomPopupPlacement[] Placements(Size size, Size target, string side)
        {
            var points = new Dictionary<string, CustomPopupPlacement>
            {
                { "Top", new CustomPopupPlacement(new Point((target.Width - size.Width) / 2, -size.Height - 6), PopupPrimaryAxis.Horizontal) },
                { "Bottom", new CustomPopupPlacement(new Point((target.Width - size.Width) / 2, target.Height + 6), PopupPrimaryAxis.Horizontal) },
                { "Left", new CustomPopupPlacement(new Point(-size.Width - 6, (target.Height - size.Height) / 2), PopupPrimaryAxis.Vertical) },
                { "Right", new CustomPopupPlacement(new Point(target.Width + 6, (target.Height - size.Height) / 2), PopupPrimaryAxis.Vertical) },
            };
            var preferred = Saved.NewsSideOrDefault(side);
            var opposite = preferred == "Top" ? "Bottom" : preferred == "Bottom" ? "Top" : preferred == "Left" ? "Right" : "Left";
            var order = new[] { preferred, opposite, "Top", "Bottom", "Right", "Left" }.Distinct();
            return order.Select(name => points[name]).ToArray();
        }

        void Reposition()
        {
            if (!popup.IsOpen) return;
            // WPF does not otherwise follow a moving owner. Offset invalidation reruns placement.
            popup.HorizontalOffset = popup.HorizontalOffset == 0 ? 0.001 : 0;
        }

        public void SetSide(string preferred)
        {
            var next = Saved.NewsSideOrDefault(preferred);
            if (side == next) return;
            side = next;
            Reposition();
        }

        public void Add(HnStory[] stories)
        {
            var oldId = story == null ? 0 : story.Id;
            pool.Clear();
            foreach (var next in stories)
            {
                if (next == null || pool.Any(s => s.Id == next.Id)) continue;
                pool.Add(next);
                if (pool.Count == 30) break;
            }
            index = pool.FindIndex(s => s.Id == oldId);
            // New front-page rankings must not interrupt the headline currently being typed.
        }

        void Advance()
        {
            if (pool.Count == 0) return;
            // Prime three distinct rows immediately; never duplicate rows for a partial fetch.
            index = story == null ? Math.Min(2, pool.Count - 1) : (index + 1) % pool.Count;
            story = pool[index];
            previous.Text = pool.Count >= 2 ? "# " + pool[(index + pool.Count - 1) % pool.Count].Title : "";
            older.Text = pool.Count >= 3 ? "# " + pool[(index + pool.Count - 2) % pool.Count].Title : "";
            previous.ToolTip = previous.Text;
            older.ToolTip = older.Text;
            previous.Visibility = previous.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            older.Visibility = older.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            characters = System.Globalization.StringInfo.ParseCombiningCharacters(story.Title);
            card.ToolTip = story.Title;
            // Reserve only the actual wrapped headline height, avoiding jumps while it types.
            var measure = new TextBlock { Text = "# " + story.Title, FontFamily = title.FontFamily,
                FontSize = title.FontSize, LineHeight = title.LineHeight, TextWrapping = TextWrapping.Wrap };
            measure.Measure(new Size(200, double.PositiveInfinity));
            title.Height = measure.DesiredSize.Height;
            title.Text = "# ▌";
            clock.Restart();
            timer.Interval = TimeSpan.FromMilliseconds(50);
        }

        public void SetVisible(bool visible, bool dim)
        {
            if (!visible || pool.Count == 0) { Hide(); return; }
            card.Opacity = dim ? 0.65 : 0.94;
            if (popup.IsOpen) return;
            if (story == null) Advance();
            clock.Start();
            popup.IsOpen = true;
            timer.Start();
        }

        void Animate()
        {
            var elapsed = clock.Elapsed.TotalSeconds;
            var count = Math.Min(characters.Length, (int)(elapsed * 28));
            var end = count == characters.Length ? story.Title.Length : characters[count];
            title.Text = "# " + story.Title.Substring(0, end) + (count < characters.Length ? "▌" : "");
            timer.Interval = TimeSpan.FromMilliseconds(count < characters.Length ? 50 : 500);
            if (ShouldAdvance(elapsed)) Advance();
        }

        bool ShouldAdvance(double elapsed)
        {
            return (pool.Count > 1 || (pool.Count == 1 && pool[0].Id != story.Id))
                && elapsed >= characters.Length / 28.0 + 6 && !card.IsMouseOver;
        }

        public void Hide() { timer.Stop(); clock.Stop(); popup.IsOpen = false; }
        public void Clear() { Hide(); pool.Clear(); story = null; index = -1; older.Text = previous.Text = title.Text = ""; }
    }

    class Pane
    {
        public string Status, Title, Tab, AgentSession;
        public long? CompletionSeq;
        public DateTime Changed;
        public bool Focused;
    }

    // Minigames in a console-style popup left of the atom (Ctrl+click; pick one in the right-click menu).
    // They only react to clicks: nothing runs while the panel is closed. An Attention state pauses them.
    abstract class MiniGame
    {
        public bool IsOpen { get { return Popup.IsOpen; } }
        protected const string Green = "#FF39FF14", Cyan = "#FF3BD5FF", Red = "#FFFF3B4E", Gold = "#FFFFC93C",
            Dim = "#FF6E8F78", Faint = "#9939FF14";
        // Box fills are opaque (the translucent tints pre-blended over the panel background) so links never show through.
        protected const string Hidden = "#FF0D280C";
        protected static readonly FontFamily Font = new FontFamily("IosevkaTerm NFM");

        public readonly Popup Popup;
        protected readonly Saved saved;
        protected readonly Action<string> flash;   // plays a skin state briefly: "Done" on a win, "Error" on a loss
        protected readonly Action changed;         // stats changed, save state.json
        protected readonly Random rng = new Random();
        protected readonly Canvas board;
        protected readonly TextBlock target, badge;   // badge: an optional game-specific indicator in the header
        readonly Border frame;
        readonly TextBlock status, message, footer;
        protected bool started, over, won, paused;
        protected string text;
        DateTime closedAt = DateTime.MinValue, lastR = DateTime.MinValue;
        const double PanelWidth = 260, Scale = 1.5;   // the whole panel is drawn 1.5x

        protected MiniGame(string title, double width, double height, UIElement anchor, Saved saved, Action<string> flash, Action changed)
        {
            this.saved = saved;
            this.flash = flash;
            this.changed = changed;
            var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 2) };
            var name = Text(title, 14, Green, FontWeights.Bold);
            target = Text("", 12, Dim, FontWeights.Normal);
            DockPanel.SetDock(name, Dock.Left);
            badge = Text("", 12, Dim, FontWeights.Bold);
            badge.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(target, Dock.Right);
            DockPanel.SetDock(badge, Dock.Right);
            target.VerticalAlignment = badge.VerticalAlignment = VerticalAlignment.Bottom;
            head.Children.Add(name);
            head.Children.Add(target);
            head.Children.Add(badge);
            status = Text("", 12, Dim, FontWeights.Normal);
            board = new Canvas { Width = width, Height = height, Margin = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };
            message = Text("", 13, Green, FontWeights.SemiBold);
            footer = Text("", 11, Dim, FontWeights.Normal);
            // Fixed width with wrapping text, and room for two message lines, so the panel never resizes mid-game.
            foreach (var t in new[] { status, message, footer }) t.TextWrapping = TextWrapping.Wrap;
            message.MinHeight = 2 * 17;
            var stack = new StackPanel { Width = Math.Max(width, PanelWidth) };
            log = Text("", 11, "#FF4FA83A", FontWeights.Normal);
            log.MinHeight = LogLines * 14;
            log.Margin = new Thickness(0, 0, 0, 6);
            log.TextTrimming = TextTrimming.CharacterEllipsis;
            typer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            typer.Tick += (s, e) => TypeLog();
            foreach (var e in new UIElement[] { head, status, board, log, message, footer }) stack.Children.Add(e);
            frame = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x08, 0x10, 0x0B)),
                BorderThickness = new Thickness(1),
                Focusable = true,
                Child = stack,
            };
            frame.LayoutTransform = new ScaleTransform(Scale, Scale);
            frame.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) Popup.IsOpen = false;
                if (e.Key != Key.R) return;
                // "rr" (two R presses within 600 ms) abandons the run and deals a new board; not counted as a loss.
                if ((DateTime.Now - lastR).TotalMilliseconds < 600) { lastR = DateTime.MinValue; NewRun(); }
                else lastR = DateTime.Now;
            };
            Popup = new Popup
            {
                AllowsTransparency = true,
                Placement = PlacementMode.Left,
                PlacementTarget = anchor,
                StaysOpen = false,
                PopupAnimation = PopupAnimation.Fade,
                Child = frame,
            };
            Popup.Opened += (s, e) => { frame.Focus(); Keyboard.Focus(frame); };
            Popup.Closed += (s, e) => closedAt = DateTime.Now;
        }

        protected static TextBlock Text(string s, double size, string color, FontWeight weight)
        {
            return new TextBlock { Text = s, FontFamily = Font, FontSize = size, FontWeight = weight, Foreground = Paint(color) };
        }

        protected static SolidColorBrush Paint(string color)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }

        protected static DropShadowEffect Glow(string color, double blur)
        {
            return new DropShadowEffect { Color = Paint(color).Color, BlurRadius = blur, ShadowDepth = 0 };
        }

        // Technobabble console, as on SS2's hack panel: each click queues a line or two, typed out quickly; the
        // typing timer only runs while lines are pending.
        const int LogLines = 4;
        readonly TextBlock log;
        readonly System.Windows.Threading.DispatcherTimer typer;
        readonly List<string> logLines = new List<string>();
        readonly Queue<string> pending = new Queue<string>();
        string typing;
        int typed;

        protected void Babble(string line)
        {
            pending.Enqueue(line);
            if (!typer.IsEnabled) typer.Start();
        }

        protected void ClearLog()
        {
            typer.Stop();
            pending.Clear();
            logLines.Clear();
            typing = null;
            log.Text = "";
        }

        void TypeLog()
        {
            if (typing == null)
            {
                if (pending.Count == 0) { typer.Stop(); return; }
                typing = pending.Dequeue();
                typed = 0;
                logLines.Add("");
                if (logLines.Count > LogLines) logLines.RemoveAt(0);
            }
            typed = Math.Min(typing.Length, typed + 2);
            logLines[logLines.Count - 1] = typing.Substring(0, typed);
            log.Text = string.Join("\n", logLines);
            if (typed == typing.Length) typing = null;
        }

        protected string Hex(int digits)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < digits; i++) sb.Append("0123456789ABCDEF"[rng.Next(16)]);
            return sb.ToString();
        }

        protected string Pick(params string[] options)
        {
            return options[rng.Next(options.Length)];
        }

        protected static string NodeId(int r, int c)
        {
            return string.Format("{0}{1}", (char)('A' + r), c + 1);
        }

        protected static string NewTarget(Random rng)
        {
            return "SEC-" + rng.Next(0x1000, 0x10000).ToString("X4");
        }

        public void Toggle()
        {
            if (Popup.IsOpen) { Popup.IsOpen = false; return; }
            // The click that closed the panel (StaysOpen=false) must not reopen it straight away.
            if ((DateTime.Now - closedAt).TotalMilliseconds < 300) return;
            if (!started || over) NewRun();
            Popup.IsOpen = true;
        }

        public void Close()
        {
            Popup.IsOpen = false;
        }

        public void SetPaused(bool value)
        {
            if (value == paused) return;
            paused = value;
            if (started) Render();
        }

        protected abstract void NewRun();
        protected abstract void Render();

        // Panel chrome shared by both games; each game draws its own board.
        protected void RenderChrome(string statusText, string playing, string statusColor = Dim)
        {
            frame.BorderBrush = Paint(paused ? Gold : Green);
            status.Text = statusText;
            status.Foreground = Paint(statusColor);
            message.Text = paused ? "PAUSED // an agent needs you" : text;
            message.Foreground = Paint(paused ? Gold : !over ? Green : won ? Cyan : Red);
            footer.Text = over ? "click a node to reboot   esc closes" : playing;
            board.Children.Clear();
        }

        protected void Record(bool win, ref int runs, ref int wins, ref int streak, ref int best)
        {
            over = true;
            won = win;
            runs++;
            if (win)
            {
                wins++;
                streak++;
                best = Math.Max(best, streak);
            }
            else streak = 0;
            changed();
            flash(win ? "Done" : "Error");
        }

        // Nodes are square boxes, as in System Shock 2's hacking grid.
        protected static Rectangle NodeBox(double size)
        {
            return new Rectangle { Width = size, Height = size, RadiusX = 2, RadiusY = 2, StrokeThickness = 1.5 };
        }

        protected void AddLine(Point a, Point b, string color, double thickness, double opacity)
        {
            board.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = Paint(color), StrokeThickness = thickness, Opacity = opacity });
        }
    }

    // ICE breaker, System Shock 2 style: light 3 nodes in a straight line (row, column or diagonal) on a 5x5 grid.
    // Each click is a breach attempt at Chance; a failure burns the node out. 3-4 nodes are visible ICE: they can be
    // breached like any other, but a failure on one trips the alarm and ends the run.
    class HackGame : MiniGame
    {
        enum Node { Unknown, Lit, Burnt }

        const int N = 5;
        const double Chance = 0.6, CellSize = 32, Dot = 20, RollMs = 280;
        static readonly List<int[]> Lines = AllLines();

        Node[,] grid;
        bool[,] ice;
        int[] line;
        int rolling = -1, tripped = -1;

        public HackGame(UIElement anchor, Saved saved, Action<string> flash, Action changed)
            : base("ICE BREAKER", N * CellSize, N * CellSize, anchor, saved, flash, changed)
        {
        }

        // Every straight run of 3 cells, as cell indexes r * N + c.
        static List<int[]> AllLines()
        {
            var lines = new List<int[]>();
            var dirs = new[] { new[] { 0, 1 }, new[] { 1, 0 }, new[] { 1, 1 }, new[] { 1, -1 } };
            for (var r = 0; r < N; r++)
                for (var c = 0; c < N; c++)
                    foreach (var d in dirs)
                    {
                        int er = r + 2 * d[0], ec = c + 2 * d[1];
                        if (er < N && ec >= 0 && ec < N)
                            lines.Add(new[] { r * N + c, (r + d[0]) * N + c + d[1], er * N + ec });
                    }
            return lines;
        }

        protected override void NewRun()
        {
            grid = new Node[N, N];
            ice = new bool[N, N];
            for (int placed = 0, total = rng.Next(3, 5); placed < total; )
            {
                int r = rng.Next(N), c = rng.Next(N);
                if (!ice[r, c]) { ice[r, c] = true; placed++; }
            }
            line = null;
            rolling = tripped = -1;
            started = true;
            over = won = false;
            target.Text = NewTarget(rng);
            text = "Light 3 in a straight line. A failed breach on ICE trips the alarm.";
            ClearLog();
            Babble(string.Format("> boot breach.exe // target {0}", target.Text));
            Render();
        }

        Node At(int k)
        {
            return grid[k / N, k % N];
        }

        void Attempt(int r, int c)
        {
            if (paused || rolling >= 0) return;
            if (over) { NewRun(); return; }
            if (grid[r, c] != Node.Unknown) return;
            rolling = r * N + c;
            Babble(Pick("> inject 0x{0} -> node {1}", "> spoof handshake {1} seq {0}", "> brute auth {1} :: key 0x{0}", "> overflow buffer @{1}+0x{0}")
                .Replace("{0}", Hex(4)).Replace("{1}", NodeId(r, c)));
            Render();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RollMs) };
            timer.Tick += (s, e) => { timer.Stop(); Resolve(r, c); };
            timer.Start();
        }

        void Resolve(int r, int c)
        {
            rolling = -1;
            if (rng.NextDouble() < Chance)
            {
                grid[r, c] = Node.Lit;
                line = Lines.FirstOrDefault(l => l.All(k => At(k) == Node.Lit));
                Babble(ice[r, c] ? "  ICE bypassed // daemon " + Hex(4) + " asleep"
                    : Pick("  ack // token " + Hex(6), "  relay " + Hex(4) + " accepted", "  node slaved, bus 0x" + Hex(2)));
                if (line != null) { End(true, "LINK ESTABLISHED // ACCESS GRANTED"); Babble("  uplink open // root shell 0x" + Hex(8)); }
                else text = ice[r, c] ? "ICE breached." : "Node breached.";
            }
            else
            {
                grid[r, c] = Node.Burnt;
                if (ice[r, c])
                {
                    tripped = r * N + c;
                    Babble("!! ICE countermeasure 0x" + Hex(4) + " armed");
                    Babble("!! alarm broadcast on bus " + Hex(2));
                    End(false, "ICE TRIPPED // ALARM RAISED");
                }
                else
                {
                    Babble(Pick("  checksum mismatch @" + Hex(4) + " // node fused", "  NAK " + Hex(2) + " // node burnt",
                        "  trace spike +" + rng.Next(3, 19) + "% // fuse blown"));
                    if (!Lines.Any(l => l.All(k => At(k) != Node.Burnt)))
                    {
                        Babble("!! no viable route // session dropped");
                        End(false, "TRACE LOST // NO LINE LEFT");
                    }
                    else text = "Node burnt out.";
                }
            }
            Render();
        }

        void End(bool win, string result)
        {
            text = result;
            Record(win, ref saved.HackRuns, ref saved.HackWins, ref saved.HackStreak, ref saved.HackBest);
        }

        Point Centre(int k)
        {
            return new Point(k % N * CellSize + CellSize / 2, k / N * CellSize + CellSize / 2);
        }

        protected override void Render()
        {
            RenderChrome(string.Format("P(breach) {0:0}%   STREAK {1}   BEST {2}", Chance * 100, saved.HackStreak, saved.HackBest),
                "click a node to breach   rr restarts   esc aborts");
            if (line != null) AddLine(Centre(line[0]), Centre(line[2]), Cyan, 4, 0.9);

            for (var r = 0; r < N; r++)
                for (var c = 0; c < N; c++)
                {
                    var k = r * N + c;
                    var dot = NodeBox(Dot);
                    var p = Centre(k);
                    Canvas.SetLeft(dot, p.X - Dot / 2);
                    Canvas.SetTop(dot, p.Y - Dot / 2);
                    var node = grid[r, c];
                    var rim = ice[r, c] ? Red : Faint;
                    if (rolling == k)
                    {
                        dot.Fill = Paint(ice[r, c] ? "#FF84252C" : "#FF21870F");
                        dot.Stroke = Brushes.White;
                    }
                    else if (node == Node.Lit)
                    {
                        var onLine = line != null && line.Contains(k);
                        dot.Fill = Paint(onLine ? Cyan : Green);
                        dot.Effect = Glow(onLine ? Cyan : Green, 10);
                        if (ice[r, c]) dot.Stroke = Paint(Red);
                    }
                    else if (node == Node.Burnt)
                    {
                        dot.Fill = Paint("#FF1C2320");
                        dot.Stroke = Paint(tripped == k ? Red : "#FF39443D");
                        if (tripped == k) dot.Effect = Glow(Red, 16);
                    }
                    else
                    {
                        dot.Fill = Paint(ice[r, c] ? "#FF461B1C" : Hidden);
                        dot.Stroke = Paint(rim);
                        if (ice[r, c]) dot.Effect = Glow(Red, 6);
                    }
                    if (node == Node.Unknown || over)
                    {
                        int row = r, col = c;
                        dot.Cursor = Cursors.Hand;
                        if (node == Node.Unknown && !over)
                        {
                            dot.MouseEnter += (s, e) => { dot.Stroke = Paint(ice[row, col] ? "#FFFF8A96" : Green); dot.StrokeThickness = 2.5; };
                            dot.MouseLeave += (s, e) => { dot.Stroke = Paint(rim); dot.StrokeThickness = 1.5; };
                        }
                        dot.MouseLeftButtonUp += (s, e) => { Attempt(row, col); e.Handled = true; };
                    }
                    board.Children.Add(dot);
                }
        }
    }

    // NET SWEEP: Minesweeper on an 8x8 network. Nodes link only to some of their up/down/left/right neighbours
    // (1-4 links); a revealed node counts the ICE on the nodes it links to. The network is a random spanning tree
    // plus extra links, so it is always connected. A probe reveals only that node. The first probe is safe and so are
    // the nodes within two links of it. A probe that completes a straight run of 3 clicked nodes joined by links (row
    // or column; nodes opened by a reveal do not count) reveals the area around the line and charges one shield (at most one); hitting ICE with a shield
    // spends it and the ICE is contained instead of ending the run. Marking exactly the ICE auto-finishes the run.
    class SweepGame : MiniGame
    {
        const int N = 8, Mines = 10;
        const double CellSize = 28, Dot = 20, ExtraLink = 0.85;
        const string Link = "#9939FF14", Rim = "#FF2FA81A", Opened = "#FF070D09";
        // Unopened boxes look raised (lit top-left, shaded bottom-right); opened boxes are flat and near-black.
        static readonly Brush Raised = new LinearGradientBrush(Color.FromRgb(0x2F, 0x7F, 0x22), Color.FromRgb(0x14, 0x3D, 0x10), new Point(0, 0), new Point(1, 1));

        bool[,] right, down, mine, open, flag, clicked, contained;   // clicked: opened by the player, not by a reveal
        int[,] count;
        bool mined;
        int boom = -1, shield;
        List<int> lastLine;   // the most recent line of 3, highlighted until the next probe

        public SweepGame(UIElement anchor, Saved saved, Action<string> flash, Action changed)
            : base("NET SWEEP", N * CellSize, N * CellSize, anchor, saved, flash, changed)
        {
        }

        protected override void NewRun()
        {
            right = new bool[N, N];
            down = new bool[N, N];
            mine = new bool[N, N];
            open = new bool[N, N];
            flag = new bool[N, N];
            clicked = new bool[N, N];
            contained = new bool[N, N];
            count = new int[N, N];
            // Randomised Kruskal: every edge joining two separate components is kept (a spanning tree), every other
            // edge only with ExtraLink, so most nodes keep 3-4 links and some drop to 1-2.
            var edges = new List<int[]>();
            for (var r = 0; r < N; r++)
                for (var c = 0; c < N; c++)
                {
                    if (c + 1 < N) edges.Add(new[] { r, c, 0 });
                    if (r + 1 < N) edges.Add(new[] { r, c, 1 });
                }
            var root = Enumerable.Range(0, N * N).ToArray();
            Func<int, int> find = null;
            find = x => root[x] == x ? x : (root[x] = find(root[x]));
            foreach (var e in edges.OrderBy(x => rng.Next()).ToList())
            {
                int a = find(e[0] * N + e[1]), b = find((e[0] + e[2]) * N + e[1] + 1 - e[2]);
                if (a != b) root[a] = b;
                else if (rng.NextDouble() >= ExtraLink) continue;
                if (e[2] == 0) right[e[0], e[1]] = true;
                else down[e[0], e[1]] = true;
            }
            mined = false;
            boom = -1;
            shield = 0;
            lastLine = null;
            started = true;
            over = won = false;
            target.Text = NewTarget(rng);
            text = "Numbers count ICE on linked nodes. 3 probes in a line charge a shield.";
            ClearLog();
            Babble(string.Format("> netscan.exe // target {0}, {1} hostile daemons", target.Text, Mines));
            Render();
        }

        List<int> Links(int r, int c)
        {
            var list = new List<int>();
            if (c + 1 < N && right[r, c]) list.Add(r * N + c + 1);
            if (c > 0 && right[r, c - 1]) list.Add(r * N + c - 1);
            if (r + 1 < N && down[r, c]) list.Add((r + 1) * N + c);
            if (r > 0 && down[r - 1, c]) list.Add((r - 1) * N + c);
            return list;
        }

        void PlaceMines(int r, int c)
        {
            var safe = new HashSet<int>(Links(r, c)) { r * N + c };
            foreach (var k in safe.ToList()) safe.UnionWith(Links(k / N, k % N));
            foreach (var k in Enumerable.Range(0, N * N).Where(k => !safe.Contains(k)).OrderBy(k => rng.Next()).Take(Mines))
                mine[k / N, k % N] = true;
            for (var i = 0; i < N; i++)
                for (var j = 0; j < N; j++)
                    count[i, j] = Links(i, j).Count(k => mine[k / N, k % N]);
            mined = true;
        }

        void Probe(int r, int c)
        {
            if (paused) return;
            if (over) { NewRun(); return; }
            if (open[r, c] || flag[r, c]) return;
            lastLine = null;
            if (!mined) PlaceMines(r, c);
            if (mine[r, c] && shield > 0)
            {
                shield = 0;
                contained[r, c] = flag[r, c] = true;
                text = "ICE CONTAINED // SHIELD SPENT";
                Babble("> probe " + NodeId(r, c) + " // hostile daemon 0x" + Hex(4) + "!");
                Babble("  firewall capacitor discharged // ICE quarantined");
                AutoFinish();
            }
            else if (mine[r, c])
            {
                boom = r * N + c;
                text = "ICE HIT // TRACE COMPLETE";
                Babble("> probe " + NodeId(r, c) + " // hostile daemon 0x" + Hex(4) + "!");
                Babble("!! trace complete // connection severed");
                Record(false, ref saved.SweepRuns, ref saved.SweepWins, ref saved.SweepStreak, ref saved.SweepBest);
            }
            else
            {
                // A probe reveals only that node. Completing a straight line of 3 revealed nodes is the reward: it
                // charges the shield and reveals the area around the line (no flood fill otherwise).
                open[r, c] = clicked[r, c] = true;
                var line = LineThrough(r, c);
                lastLine = line;
                var charged = line != null && shield == 0;
                if (charged) shield = 1;
                var revealed = line == null ? 0 : RevealAround(line);
                Babble(string.Format(Pick("> probe {0} // {1} hostile on linked bus", "> ping {0} :: {1} signatures, ttl {2}",
                    "> sniff {0} // {1} daemons on {3} links"), NodeId(r, c), count[r, c], rng.Next(16, 129), Links(r, c).Count));
                if (line != null) Babble("  route locked // mapped " + revealed + " adjacent nodes");
                if (charged) Babble("  firewall capacitor charged");
                var opened = 0;
                foreach (var o in open) if (o) opened++;
                if (opened == N * N - Mines)
                {
                    text = "NETWORK SWEPT // ACCESS GRANTED";
                    Babble("  all safe nodes mapped // root 0x" + Hex(8));
                    Record(true, ref saved.SweepRuns, ref saved.SweepWins, ref saved.SweepStreak, ref saved.SweepBest);
                }
                else if (line != null)
                    text = "LINE OF 3 // " + revealed + " nodes revealed" + (charged ? ", SHIELD CHARGED" : "");
                else text = "Probe returned " + count[r, c] + ".";
            }
            Render();
        }

        // Reveals the ICE-free nodes linked to the line, then keeps opening along links from any revealed node that
        // counts no ICE (Minesweeper's area reveal, only as a line reward). Returns how many nodes it opened.
        int RevealAround(List<int> line)
        {
            var revealed = 0;
            var queue = new Queue<int>(line);
            var fromLine = new HashSet<int>(line);
            while (queue.Count > 0)
            {
                var at = queue.Dequeue();
                if (!fromLine.Contains(at) && count[at / N, at % N] > 0) continue;
                foreach (var k in Links(at / N, at % N))
                {
                    int i = k / N, j = k % N;
                    if (open[i, j] || flag[i, j] || mine[i, j]) continue;
                    open[i, j] = true;
                    revealed++;
                    queue.Enqueue(k);
                }
            }
            return revealed;
        }

        // The first straight run of 3 clicked nodes through (r, c) along links (a row or column, no diagonals), or null. Nodes opened
        // by a reveal do not count, so a reward cannot chain into the next one.
        List<int> LineThrough(int r, int c)
        {
            foreach (var d in new[] { new[] { 0, 1 }, new[] { 1, 0 } })
                for (var shift = -2; shift <= 0; shift++)
                {
                    var line = new List<int>();
                    for (var i = 0; i < 3; i++)
                    {
                        int nr = r + (shift + i) * d[0], nc = c + (shift + i) * d[1];
                        if (nr < 0 || nr >= N || nc < 0 || nc >= N || !clicked[nr, nc]) break;
                        // Each step must follow a link: right[] joins (r, c)-(r, c+1), down[] joins (r, c)-(r+1, c).
                        if (i > 0 && !(d[0] == 0 ? right[nr, nc - 1] : down[nr - 1, nc])) break;
                        line.Add(nr * N + nc);
                    }
                    if (line.Count == 3) return line;
                }
            return null;
        }

        void Mark(int r, int c)
        {
            if (paused || over || open[r, c] || contained[r, c]) return;
            flag[r, c] = !flag[r, c];
            Babble(flag[r, c] ? "> tag " + NodeId(r, c) + " hostile" : "> untag " + NodeId(r, c));
            AutoFinish();
            Render();
        }

        // Auto-finish: once the marks (contained ICE included) cover exactly the ICE, the remaining safe nodes open and
        // the run is won. If the count reaches the ICE total and nothing happens, a mark is wrong (an accepted hint).
        void AutoFinish()
        {
            if (!mined) return;   // no ICE placed before the first probe
            for (var i = 0; i < N; i++)
                for (var j = 0; j < N; j++)
                    if (flag[i, j] != mine[i, j]) return;
            for (var i = 0; i < N; i++)
                for (var j = 0; j < N; j++)
                    if (!mine[i, j]) open[i, j] = true;
            text = "ALL ICE MARKED // NETWORK SWEPT";
            Babble("  all daemons tagged // sweeping clean nodes");
            Record(true, ref saved.SweepRuns, ref saved.SweepWins, ref saved.SweepStreak, ref saved.SweepBest);
        }

        Point Centre(int r, int c)
        {
            return new Point(c * CellSize + CellSize / 2, r * CellSize + CellSize / 2);
        }

        protected override void Render()
        {
            var marked = 0;
            foreach (var f in flag) if (f) marked++;
            badge.Text = shield > 0 ? "SHIELD \u25A0" : "SHIELD \u25A1";
            badge.Foreground = Paint(shield > 0 ? Cyan : Dim);
            badge.Effect = shield > 0 ? Glow(Cyan, 8) : null;
            // ICE LEFT = ICE minus marks (contained ICE counts as marked); below zero a mark is certainly wrong.
            var left = Mines - marked;
            RenderChrome(string.Format("ICE LEFT {0}{1}   STREAK {2}   BEST {3}", left, left < 0 ? " (a mark is wrong)" : "", saved.SweepStreak, saved.SweepBest),
                "click probes   right-click marks ICE   rr restarts   esc aborts", left < 0 ? Red : Dim);

            for (var r = 0; r < N; r++)
                for (var c = 0; c < N; c++)
                {
                    if (c + 1 < N && right[r, c]) AddLine(Centre(r, c), Centre(r, c + 1), Link, 2.5, 1);
                    if (r + 1 < N && down[r, c]) AddLine(Centre(r, c), Centre(r + 1, c), Link, 2.5, 1);
                }
            if (lastLine != null)
                AddLine(Centre(lastLine[0] / N, lastLine[0] % N), Centre(lastLine[2] / N, lastLine[2] % N), Cyan, 4, 0.9);

            for (var r = 0; r < N; r++)
                for (var c = 0; c < N; c++)
                {
                    var dot = NodeBox(Dot);
                    var p = Centre(r, c);
                    Canvas.SetLeft(dot, p.X - Dot / 2);
                    Canvas.SetTop(dot, p.Y - Dot / 2);
                    string label = null, ink = Green;
                    if (open[r, c] && lastLine != null && lastLine.Contains(r * N + c))
                    {
                        dot.Fill = Paint(Cyan);
                        dot.Effect = Glow(Cyan, 10);
                        if (count[r, c] > 0) label = count[r, c].ToString();
                        ink = "#FF08100B";
                    }
                    else if (open[r, c])
                    {
                        dot.Fill = Paint(Opened);
                        // Bright rim: clicked (counts toward a line); dim rim: opened by a reveal.
                        dot.Stroke = Paint(clicked[r, c] ? Green : "#6639FF14");
                        if (count[r, c] > 0) label = count[r, c].ToString();
                    }
                    else if (contained[r, c])
                    {
                        dot.Fill = Paint(Red);
                        dot.Effect = Glow(Red, 8);
                    }
                    else if (over && mine[r, c])
                    {
                        dot.Fill = Paint(won ? Cyan : Red);
                        dot.Effect = Glow(won ? Cyan : Red, boom == r * N + c ? 16 : 8);
                        if (boom == r * N + c) dot.Stroke = Brushes.White;
                    }
                    else if (flag[r, c])
                    {
                        dot.Fill = Paint("#FF39191A");
                        dot.Stroke = Paint(Red);
                        label = "!";
                        ink = Red;
                    }
                    else
                    {
                        dot.Fill = Raised;
                        dot.Stroke = Paint(Rim);
                        if (!over)
                        {
                            dot.MouseEnter += (s, e) => { dot.Stroke = Paint(Green); dot.StrokeThickness = 2.5; };
                            dot.MouseLeave += (s, e) => { dot.Stroke = Paint(Rim); dot.StrokeThickness = 1.5; };
                        }
                    }
                    int row = r, col = c;
                    dot.Cursor = Cursors.Hand;
                    dot.MouseLeftButtonUp += (s, e) => { Probe(row, col); e.Handled = true; };
                    dot.MouseRightButtonUp += (s, e) => { Mark(row, col); e.Handled = true; };
                    board.Children.Add(dot);
                    if (label == null) continue;
                    var t = Text(label, 12, ink, FontWeights.Bold);
                    t.Width = Dot;
                    t.TextAlignment = TextAlignment.Center;
                    t.IsHitTestVisible = false;
                    Canvas.SetLeft(t, p.X - Dot / 2);
                    Canvas.SetTop(t, p.Y - 8);
                    board.Children.Add(t);
                }
        }
    }

    class App
    {
        // The hover status popup is disabled; keep the implementation for future use.
        static readonly bool HoverStatusToolTipEnabled = false;

        static readonly string[] AllowedUris =
        {
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
            "http://schemas.microsoft.com/winfx/2006/xaml",
            "http://schemas.openxmlformats.org/markup-compatibility/2006",
            "http://schemas.microsoft.com/expression/blend/2008",
        };
        static readonly string[] DeniedTokens =
        {
            "ObjectDataProvider", "XmlDataProvider", "x:Code", "x:Class", "clr-namespace", "assembly=",
            "EventSetter", "Frame", "WebBrowser", "MediaElement",
        };
        static readonly Dictionary<string, string> StateNames = new Dictionary<string, string>
        {
            { "idle", "Idle" }, { "busy", "Busy" }, { "attention", "Attention" }, { "error", "Error" }, { "done", "Done" },
        };
        // Size presets in DIPs (scaled by the display's DPI): Small is twice a Windows 11 taskbar icon;
        // Medium and Large stay 2x and 4x Small; Very large is 1.5x Large.
        static readonly KeyValuePair<string, double>[] SizePresets =
        {
            new KeyValuePair<string, double>("Small", 48),
            new KeyValuePair<string, double>("Medium", 96),
            new KeyValuePair<string, double>("Large", 192),
            new KeyValuePair<string, double>("Very large", 288),
        };
        const double MinSize = 24, MaxSize = 800;
        static readonly Regex TabPattern = new Regex("^[A-Za-z0-9]+:[A-Za-z0-9]+$");

        readonly string root, statePath, logPath, herdrExe;
        readonly Config cfg;
        readonly Saved saved;
        readonly ConcurrentQueue<InboxItem> inbox = new ConcurrentQueue<InboxItem>();
        volatile bool running = true;
        HttpListener listener;

        Window window;
        Viewbox skinBox;
        Ellipse hitZone;
        Thumb grip;
        Popup bubble;
        TextBlock bubbleText;
        ContextMenu menu;
        IntPtr mouseHook, windowHandle;
        Win32.MouseHookProc mouseHookProc;
        bool rightClickPassing, rightButtonHeld, mouseRoutingError;
        HnTicker newsTicker;
        readonly HnFeed newsFeed = new HnFeed();
        readonly Random newsRandom = new Random();
        DateTime nextNews = DateTime.MaxValue;
        CancellationTokenSource newsCancel;
        int newsGeneration, newsFetching, newsFailures;
        DateTime bubbleUntil = DateTime.MinValue;
        DateTime revealUntil = DateTime.MinValue;   // a ping shows a dimmed Adjutant at full opacity until then
        bool terminalWasInFront;
        bool dirty;
        int ticks;

        FrameworkElement skinRoot;
        Storyboard motionBoard, stateBoard;
        string currentState, skinFile;
        DateTime skinStamp;

        Dictionary<string, Pane> panes = new Dictionary<string, Pane>();
        bool seenAgents;
        string herdrError, lastPingTab, overrideState;
        DateTime overrideUntil = DateTime.MinValue;
        DateTime focusedDoneUntil = DateTime.MinValue;
        bool focusedDoneRestart;
        MiniGame[] games;
        MiniGame Game { get { return saved.Minigame == "sweep" ? games[1] : games[0]; } }
        string gameFlash;
        DateTime gameFlashUntil = DateTime.MinValue;
        bool gameFlashRestart, gameClick;
        readonly List<string> history = new List<string>();

        App(string skinArg)
        {
            root = AppDomain.CurrentDomain.BaseDirectory;
            cfg = Config.Load(Path.Combine(root, "config.json"));
            var stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "adjutant");
            Directory.CreateDirectory(stateDir);
            statePath = Path.Combine(stateDir, "state.json");
            logPath = Path.Combine(stateDir, "adjutant.log");
            try { saved = Saved.Load(statePath, cfg); }
            catch (Exception ex)
            {
                Log("state.json unreadable, using defaults: " + ex.Message);
                saved = new Saved { Size = cfg.Size, Skin = cfg.Skin, Fps = Saved.FrameRateOrDefault(cfg.Fps) };
            }
            if (!string.IsNullOrEmpty(skinArg)) saved.Skin = skinArg;
            herdrExe = FindOnPath("herdr.exe");
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "Local\\Adjutant", out created))
            {
                if (!created) return;
                var app = new App(args.Length > 0 ? args[0] : null);
                app.Run();
            }
        }

        void Log(string text)
        {
            try { File.AppendAllText(logPath, string.Format("{0:yyyy-MM-dd HH:mm:ss} {1}\r\n", DateTime.Now, text)); }
            catch (IOException) { }
        }

        static string FindOnPath(string exe)
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (dir.Length == 0) continue;
                try
                {
                    var p = Path.Combine(dir.Trim('"'), exe);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        // ------------------------------------------------------------ background workers
        // Accepts POST /ping only from local, non-browser callers: a custom header is required (browsers
        // would need a CORS preflight we never answer) and any Origin header is refused.
        void HttpLoop()
        {
            var l = new HttpListener();
            l.Prefixes.Add("http://127.0.0.1:" + cfg.Port + "/");
            try { l.Start(); }
            catch (Exception ex)
            {
                inbox.Enqueue(new InboxItem { Kind = "fault", Msg = "listener on port " + cfg.Port + " failed: " + ex.Message });
                return;
            }
            listener = l;
            while (running)
            {
                HttpListenerContext ctx;
                try { ctx = l.GetContext(); }
                catch (Exception) { break; }
                int status;
                try { status = HandleRequest(ctx.Request); }
                catch (Exception) { status = 400; }
                try { ctx.Response.StatusCode = status; ctx.Response.Close(); }
                catch (Exception) { }
            }
        }

        int HandleRequest(HttpListenerRequest req)
        {
            var path = req.Url.AbsolutePath;
            if (path == "/health" && req.HttpMethod == "GET") return 200;
            if (path != "/ping" || req.HttpMethod != "POST") return 404;
            if (req.Headers["Origin"] != null || req.Headers["X-Adjutant"] != "1") return 403;
            if (req.ContentLength64 < 1 || req.ContentLength64 > 4096) return 413;

            string text;
            using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) text = r.ReadToEnd();
            var body = Json.Object(Json.Serializer.DeserializeObject(text));
            if (body == null) return 400;
            foreach (var key in new[] { "from", "msg", "state", "tab" })
            {
                var v = Json.Get(body, key);
                if (v != null && !(v is string)) return 400;
            }
            string from = Json.Str(body, "from"), msg = Json.Str(body, "msg");
            string state = Json.Str(body, "state"), tab = Json.Str(body, "tab");
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(msg)) return 400;
            if (!string.IsNullOrEmpty(state) && !StateNames.ContainsKey(state)) return 400;
            if (!string.IsNullOrEmpty(tab) && !TabPattern.IsMatch(tab)) return 400;
            inbox.Enqueue(new InboxItem
            {
                Kind = "ping",
                From = from.Substring(0, Math.Min(40, from.Length)),
                Msg = msg.Substring(0, Math.Min(300, msg.Length)),
                State = string.IsNullOrEmpty(state) ? null : state,
                Tab = string.IsNullOrEmpty(tab) ? null : tab,
            });
            return 204;
        }

        void HerdrLoop()
        {
            while (running)
            {
                try
                {
                    if (herdrExe == null) throw new InvalidOperationException("herdr.exe not found on PATH");
                    var psi = new ProcessStartInfo(herdrExe, "agent list")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                    };
                    using (var p = Process.Start(psi))
                    {
                        var err = p.StandardError.ReadToEndAsync();
                        var output = p.StandardOutput.ReadToEnd();
                        if (!p.WaitForExit(10000)) p.Kill();
                        err.Wait(1000);
                        var result = Json.Object(Json.Get(Json.Object(Json.Serializer.DeserializeObject(output)), "result"));
                        var agents = Json.Get(result, "agents") as object[];
                        if (agents == null) throw new InvalidDataException("no agents in herdr output");
                        inbox.Enqueue(new InboxItem { Kind = "agents", Agents = agents });
                    }
                }
                catch (Exception ex)
                {
                    inbox.Enqueue(new InboxItem { Kind = "agents", Agents = null, Error = ex.Message });
                }
                Thread.Sleep(cfg.PollSeconds * 1000);
            }
        }

        static void StartThread(ThreadStart body)
        {
            new Thread(body) { IsBackground = true }.Start();
        }

        // ------------------------------------------------------------ window
        void BuildWindow()
        {
            window = new Window
            {
                Title = "Adjutant",
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = null,
                Topmost = true,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
                MinWidth = MinSize,
                MinHeight = MinSize,
            };
            var grid = new Grid();
            // Transparent pixels are click-through, so the gaps in the skin would miss clicks. This centred
            // disc is invisible (alpha 1/255) but still catches the mouse; its size is hitZone x the window.
            hitZone = new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
            grid.Children.Add(hitZone);
            skinBox = new Viewbox { Stretch = Stretch.Uniform };
            ToolTipService.SetIsEnabled(skinBox, HoverStatusToolTipEnabled);
            grid.Children.Add(skinBox);
            // A large custom grip: the built-in ResizeGrip is a few pixels wide and needs pixel-perfect aim.
            grip = (Thumb)XamlReader.Parse(
                "<Thumb xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' HorizontalAlignment='Right'" +
                " VerticalAlignment='Bottom' Cursor='SizeNWSE' Opacity='0.35' ToolTip='Drag to resize (or Ctrl+wheel)'>" +
                "<Thumb.Template><ControlTemplate TargetType='Thumb'><Grid Background='#01000000'>" +
                "<Viewbox><Path Data='M 26,6 L 26,26 L 6,26 Z' Fill='#FF39FF14'/></Viewbox>" +
                "</Grid></ControlTemplate></Thumb.Template></Thumb>");
            grid.Children.Add(grip);
            window.Content = grid;

            var size = Math.Max(MinSize, saved.Size);
            window.Width = size;
            window.Height = size;
            double vsLeft = SystemParameters.VirtualScreenLeft, vsTop = SystemParameters.VirtualScreenTop;
            double vsRight = vsLeft + SystemParameters.VirtualScreenWidth, vsBottom = vsTop + SystemParameters.VirtualScreenHeight;
            if (saved.Left.HasValue && saved.Top.HasValue && saved.Left >= vsLeft && saved.Top >= vsTop
                && saved.Left + 24 <= vsRight && saved.Top + 24 <= vsBottom)
            {
                window.Left = saved.Left.Value;
                window.Top = saved.Top.Value;
            }
            else
            {
                var home = HomePosition(size);
                window.Left = home.X;
                window.Top = home.Y;
            }

            bubbleText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300,
                FontFamily = new FontFamily("IosevkaTerm NFM"), // matches the console skins
                FontSize = 13,
                Foreground = Brush("#FFE8FFF4"),
            };
            var bubbleBorder = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 10, 6),
                Background = Brush("#E60E1A14"),
                BorderBrush = Brush("#FF39FF14"),
                BorderThickness = new Thickness(1),
                Child = bubbleText,
            };
            bubble = new Popup
            {
                AllowsTransparency = true,
                Placement = PlacementMode.Top,
                PlacementTarget = skinBox,
                Child = bubbleBorder,
            };
            bubbleBorder.MouseLeftButtonUp += (s, e) => Click();
            newsTicker = new HnTicker(skinBox);
            Action<string> gameResult = state => { gameFlash = state; gameFlashUntil = DateTime.Now.AddSeconds(2.5); gameFlashRestart = true; };
            Action gameStats = () => dirty = true;
            games = new MiniGame[] { new HackGame(skinBox, saved, gameResult, gameStats), new SweepGame(skinBox, saved, gameResult, gameStats) };

            window.MouseLeftButtonDown += (s, e) =>
            {
                // Ctrl+click toggles the game on button up: a StaysOpen=false popup opened on button down
                // closes again straight away.
                if (Win32.CtrlDown()) { gameClick = true; e.Handled = true; return; }
                double left = window.Left, top = window.Top;
                try { window.DragMove(); }
                catch (InvalidOperationException) { }
                var moved = Math.Abs(window.Left - left) + Math.Abs(window.Top - top);
                if (moved < 4) Click();
                else { dirty = true; bubble.IsOpen = false; }
            };
            window.MouseLeftButtonUp += (s, e) =>
            {
                if (!gameClick) return;
                gameClick = false;
                bubble.IsOpen = false;
                Game.Toggle();
                e.Handled = true;
            };
            window.MouseWheel += (s, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                SetSize(window.Width * (e.Delta > 0 ? 1.1 : 1 / 1.1), false);
                e.Handled = true;
            };
            window.SizeChanged += (s, e) => dirty = true;
            // Less-distraction mode: rest at a low opacity, fade to full while the pointer is over the
            // Adjutant. Faded pixels keep a non-zero alpha, so hovering still registers.
            window.MouseEnter += (s, e) => FadeTo(1.0);
            window.MouseLeave += (s, e) => FadeToRestIfIdle();

            grip.MouseEnter += (s, e) => grip.Opacity = 1;
            grip.MouseLeave += (s, e) => grip.Opacity = 0.35;
            grip.DragDelta += (s, e) =>
            {
                SetSize(window.Width + Math.Max(e.HorizontalChange, e.VerticalChange), false);
                e.Handled = true;
            };
            SetSize(window.Width, false);

            BuildMenu();
            window.SourceInitialized += (s, e) =>
            {
                windowHandle = new WindowInteropHelper(window).Handle;
                mouseHookProc = RouteRightClick;
                // The callback only changes this layered window's routing; no input is recorded or synthesized.
                mouseHook = Win32.SetWindowsHookEx(14, mouseHookProc, Win32.GetModuleHandle(null), 0);
                if (mouseHook == IntPtr.Zero) Log("right-click routing hook unavailable");
            };
            window.ContextMenuOpening += (s, e) =>
            {
                // Also guard WPF/keyboard context requests; only a pointer over the core opens this menu.
                if (!IsInCore(window.PointToScreen(Mouse.GetPosition(window)))) e.Handled = true;
            };
            FadeTo(RestOpacity());
        }

        bool IsInCore(Point screenPoint)
        {
            var core = skinRoot == null ? null : skinRoot.FindName("Core") as FrameworkElement;
            if (core == null || core.ActualWidth <= 0 || core.ActualHeight <= 0) return false;
            var p = core.PointFromScreen(screenPoint);
            var x = (p.X - core.ActualWidth / 2) / (core.ActualWidth / 2);
            var y = (p.Y - core.ActualHeight / 2) / (core.ActualHeight / 2);
            return x * x + y * y <= 1;
        }

        IntPtr RouteRightClick(int code, IntPtr message, IntPtr data)
        {
            try { RouteRightClickCore(code, message, data); }
            catch (Exception)
            {
                // Never let layout/skin errors escape a native input callback or leave left clicks transparent.
                Win32.SetMouseTransparent(windowHandle, false);
                rightClickPassing = rightButtonHeld = false;
                mouseRoutingError = true;
            }
            return Win32.CallNextHookEx(mouseHook, code, message, data);
        }

        void RouteRightClickCore(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && windowHandle != IntPtr.Zero)
            {
                var kind = message.ToInt32();
                const int rightDown = 0x204, rightUp = 0x205, mouseMove = 0x200;
                if (kind != rightDown && !rightClickPassing) return;
                // MSLLHOOKSTRUCT begins with a physical screen POINT (also on mixed-DPI/negative monitors).
                var point = new Win32.ScreenPoint { X = Marshal.ReadInt32(data), Y = Marshal.ReadInt32(data, 4) };
                if (kind == rightDown)
                {
                    // End an earlier pass-through before choosing the target for a new gesture.
                    if (rightClickPassing) Win32.SetMouseTransparent(windowHandle, false);
                    rightClickPassing = rightButtonHeld = false;
                    Win32.WindowRect bounds;
                    if (!menu.IsOpen && Win32.GetWindowRect(windowHandle, out bounds)
                        && point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom
                        && Win32.WindowFromPoint(point) == windowHandle
                        && !IsInCore(new Point(point.X, point.Y))
                        && (Win32.GetAsyncKeyState(0x01) & 0x8000) == 0)
                    {
                        Win32.SetMouseTransparent(windowHandle, true);
                        rightClickPassing = rightButtonHeld = true;
                    }
                }
                else if (kind == rightUp)
                {
                    // A left/middle/wheel chord may have temporarily restored normal routing.
                    // The original outside-core right gesture still owns its matching button-up.
                    if (rightClickPassing) Win32.SetMouseTransparent(windowHandle, true);
                    rightButtonHeld = false;
                }
                else if (rightClickPassing)
                {
                    // Let other buttons/wheel retain their normal target without losing the right gesture.
                    // Moves while right is held stay transparent; the first move after release restores hover.
                    Win32.SetMouseTransparent(windowHandle, kind == mouseMove && rightButtonHeld);
                    if (!rightButtonHeld) rightClickPassing = false;
                }
            }
        }

        static SolidColorBrush Brush(string color)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }

        static Point HomePosition(double size)
        {
            var work = SystemParameters.WorkArea;
            var margin = Math.Min(24, Math.Max(8, size / 4));
            return new Point(work.Right - size - margin, work.Top + (work.Height - size) / 2);
        }

        // Resizes the square window. keepBottomRight grows or shrinks toward the top-left, so an Adjutant
        // parked in the bottom-right corner stays there; the grip and Ctrl+wheel keep the top-left fixed.
        void SetSize(double size, bool keepBottomRight)
        {
            size = Math.Min(MaxSize, Math.Max(MinSize, size));
            if (keepBottomRight)
            {
                window.Left += window.Width - size;
                window.Top += window.Height - size;
            }
            window.Width = size;
            window.Height = size;
            hitZone.Width = size * cfg.HitZone;
            hitZone.Height = size * cfg.HitZone;
            var g =Math.Max(8, Math.Min(28, size * 0.3));
            grip.Width = g;
            grip.Height = g;
            grip.Visibility = saved.Grip ? Visibility.Visible : Visibility.Collapsed;
        }

        double RestOpacity() { return saved.Dim ? cfg.DimOpacity : 1.0; }

        const int UntilHerdr = -1;

        // "Until herdr is open" keeps the bubble until Windows Terminal comes to the front (see Tick). If it is
        // already in front when the message arrives, that can't happen, so the standard time applies instead.
        DateTime BubbleExpiry()
        {
            if (saved.BubbleSeconds == UntilHerdr)
                return Win32.TerminalInFront() ? DateTime.Now.AddSeconds(cfg.BubbleSeconds) : DateTime.MaxValue;
            return DateTime.Now.AddSeconds(saved.BubbleSeconds > 0 ? saved.BubbleSeconds : cfg.BubbleSeconds);
        }

        // Fades back to the rest opacity unless something still wants it fully visible:
        // the pointer, the open menu, or a recent ping.
        void FadeToRestIfIdle()
        {
            if (window.IsMouseOver || menu.IsOpen || DateTime.Now < revealUntil) return;
            FadeTo(RestOpacity());
        }

        void Reveal()
        {
            revealUntil = DateTime.Now.AddSeconds(cfg.OverrideSeconds);
            FadeTo(1.0);
        }

        void FadeTo(double to)
        {
            var fade = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(150)));
            Timeline.SetDesiredFrameRate(fade, saved.Fps);
            window.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        void BuildMenu()
        {
            menu = new ContextMenu();

            var sizeMenu = new MenuItem { Header = "Size" };
            foreach (var preset in SizePresets)
            {
                var item = new MenuItem { Header = string.Format("{0} ({1})", preset.Key, preset.Value), Tag = preset.Value, IsCheckable = true };
                item.Click += (s, e) => { SetSize((double)((MenuItem)s).Tag, true); bubble.IsOpen = false; };
                sizeMenu.Items.Add(item);
            }
            sizeMenu.SubmenuOpened += (s, e) =>
            {
                foreach (MenuItem item in sizeMenu.Items) item.IsChecked = (double)item.Tag == window.Width;
            };

            var frameMenu = new MenuItem { Header = "Frame rate" };
            foreach (var fps in new[] { 24, 30, 60 })
            {
                var label = fps + " FPS";
                var item = new MenuItem { Header = label, ToolTip = fps == 24 ? "Efficient" : fps == 30 ? "Balanced" : "Smooth",
                    Tag = fps, IsCheckable = true, IsChecked = fps == saved.Fps };
                item.Click += (s, e) =>
                {
                    try { SetFrameRate((int)((MenuItem)s).Tag); }
                    catch (Exception ex) { AddEvent("adjutant", "frame rate change failed: " + ex.Message, null); }
                    finally
                    {
                        foreach (MenuItem choice in frameMenu.Items) choice.IsChecked = (int)choice.Tag == saved.Fps;
                    }
                };
                frameMenu.Items.Add(item);
            }
            frameMenu.SubmenuOpened += (s, e) =>
            {
                foreach (MenuItem item in frameMenu.Items) item.IsChecked = (int)item.Tag == saved.Fps;
            };

            var homeItem = new MenuItem { Header = "Reset position", ToolTip = "Center-right of the primary screen" };
            homeItem.Click += (s, e) =>
            {
                var p = HomePosition(window.Width);
                window.Left = p.X;
                window.Top = p.Y;
                bubble.IsOpen = false;
                dirty = true;
            };

            var gripItem = new MenuItem { Header = "Show resize handle", IsCheckable = true, IsChecked = saved.Grip };
            gripItem.Click += (s, e) => { saved.Grip = gripItem.IsChecked; SetSize(window.Width, false); dirty = true; };

            var dimItem = new MenuItem
            {
                Header = "Less distraction",
                ToolTip = string.Format("Rest at {0}% opacity; reveal on hover", (int)Math.Round(cfg.DimOpacity * 100)),
                IsCheckable = true,
                IsChecked = saved.Dim,
            };
            dimItem.Click += (s, e) => { saved.Dim = dimItem.IsChecked; dirty = true; };

            var durationMenu = new MenuItem { Header = "Message duration" };
            foreach (var preset in new[] { 0, 30, 60, UntilHerdr })
            {
                var label = preset == 0 ? string.Format("Standard ({0} s)", cfg.BubbleSeconds)
                    : preset == 30 ? "30 seconds" : preset == 60 ? "1 minute" : "Until herdr is open";
                var item = new MenuItem { Header = label, Tag = preset, IsCheckable = true };
                item.Click += (s, e) =>
                {
                    saved.BubbleSeconds = (int)((MenuItem)s).Tag;
                    dirty = true;
                    if (bubble.IsOpen) bubbleUntil = BubbleExpiry();
                };
                durationMenu.Items.Add(item);
            }
            durationMenu.SubmenuOpened += (s, e) =>
            {
                foreach (MenuItem item in durationMenu.Items) item.IsChecked = (int)item.Tag == saved.BubbleSeconds;
            };

            var quietItem = new MenuItem { Header = "Quiet", ToolTip = "Hide message bubbles and news", IsCheckable = true, IsChecked = saved.Quiet };
            quietItem.Click += (s, e) => { saved.Quiet = quietItem.IsChecked; dirty = true; if (saved.Quiet) bubble.IsOpen = false; };

            var newsItem = new MenuItem { Header = "HN newswire", ToolTip = "An occasional front-page headline, display only",
                IsCheckable = true, IsChecked = saved.HnNews };
            newsItem.Click += (s, e) => { saved.HnNews = newsItem.IsChecked; ResetNews(); dirty = true; };

            var newsPosition = new MenuItem { Header = "Newswire position", ToolTip = "Preferred side; adjusts automatically at screen edges" };
            foreach (var side in new[] { "Top", "Left", "Right", "Bottom" })
            {
                var choice = new MenuItem { Header = side, Tag = side, IsCheckable = true, IsChecked = saved.NewsSide == side };
                choice.Click += (s, e) =>
                {
                    saved.NewsSide = (string)((MenuItem)s).Tag;
                    newsTicker.SetSide(saved.NewsSide);
                    dirty = true;
                };
                newsPosition.Items.Add(choice);
            }
            newsPosition.SubmenuOpened += (s, e) =>
            {
                foreach (MenuItem choice in newsPosition.Items) choice.IsChecked = (string)choice.Tag == saved.NewsSide;
            };

            var gameMenu = new MenuItem { Header = "Minigame", ToolTip = "Ctrl+click to play" };
            foreach (var pick in new[] { new[] { "ice", "ICE breaker" }, new[] { "sweep", "NET SWEEP" } })
            {
                var item = new MenuItem { Header = pick[1], Tag = pick[0], IsCheckable = true };
                item.Click += (s, e) =>
                {
                    foreach (var g in games) g.Close();
                    saved.Minigame = (string)((MenuItem)s).Tag;
                    dirty = true;
                };
                gameMenu.Items.Add(item);
            }
            gameMenu.SubmenuOpened += (s, e) =>
            {
                foreach (MenuItem item in gameMenu.Items) item.IsChecked = (string)item.Tag == saved.Minigame;
            };

            var skinMenu = new MenuItem { Header = "Skin" };
            skinMenu.Items.Add(new MenuItem { Header = "(loading)" });
            skinMenu.SubmenuOpened += (s, e) =>
            {
                if (e.OriginalSource != skinMenu) return;
                skinMenu.Items.Clear();
                foreach (var dir in Directory.GetDirectories(Path.Combine(root, "skins")).OrderBy(d => d))
                {
                    if (!File.Exists(Path.Combine(dir, "skin.xaml"))) continue;
                    var name = Path.GetFileName(dir);
                    var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = name == saved.Skin };
                    item.Click += (s2, e2) =>
                    {
                        try { ImportSkin((string)((MenuItem)s2).Header); dirty = true; }
                        catch (Exception ex) { AddEvent("adjutant", ex.Message, null); }
                    };
                    skinMenu.Items.Add(item);
                }
            };

            var reloadItem = new MenuItem { Header = "Reload skin" };
            reloadItem.Click += (s, e) =>
            {
                try { ImportSkin(saved.Skin); }
                catch (Exception ex) { AddEvent("adjutant", ex.Message, null); }
            };

            var quitItem = new MenuItem { Header = "Quit" };
            quitItem.Click += (s, e) => window.Close();

            foreach (var i in new[] { sizeMenu, frameMenu, homeItem, gripItem, dimItem, durationMenu, quietItem, newsItem, newsPosition, gameMenu, skinMenu, reloadItem }) menu.Items.Add(i);
            menu.Items.Add(new Separator());
            menu.Items.Add(quitItem);
            menu.Closed += (s, e) => FadeToRestIfIdle();
            menu.Opened += (s, e) => newsTicker.Hide();
            window.ContextMenu = menu;
        }

        // ------------------------------------------------------------ skin
        Storyboard PrepareBoard(Storyboard template, int? fps = null)
        {
            if (template == null) return null;
            var board = template.Clone();
            Timeline.SetDesiredFrameRate(board, fps ?? saved.Fps);
            return board;
        }

        Storyboard Reclock(Storyboard board, double speed, int fps)
        {
            if (board == null) return null;
            var time = board.GetCurrentTime(skinRoot);
            // A newly begun clock can have no time until its first tick; it still needs the new cap.
            var paused = board.GetIsPaused(skinRoot);
            var replacement = PrepareBoard(board, fps);
            board.Remove(skinRoot);
            replacement.Begin(skinRoot, true);
            replacement.SetSpeedRatio(skinRoot, speed);
            if (time.HasValue) replacement.SeekAlignedToLastTick(skinRoot, time.Value, TimeSeekOrigin.BeginTime);
            if (paused) replacement.Pause(skinRoot);
            return replacement;
        }

        void SetFrameRate(int fps)
        {
            if (fps != 24 && fps != 30 && fps != 60) throw new ArgumentOutOfRangeException("fps");
            if (saved.Fps == fps) return;
            // New root clocks are required by WPF; seek rather than replay the current animations.
            try
            {
                motionBoard = Reclock(motionBoard, cfg.Speeds[currentState ?? "Idle"], fps);
                // State clocks use their normal speed; only Motion has a per-agent-state speed ratio.
                stateBoard = Reclock(stateBoard, 1.0, fps);
            }
            catch
            {
                // An exceptional skin/clock failure restores the old rate instead of persisting a partial change.
                try { ImportSkin(saved.Skin); }
                catch (Exception ex) { Log("frame rate restore failed: " + ex.Message); }
                throw;
            }
            saved.Fps = fps;
            dirty = true;
        }

        void ImportSkin(string name)
        {
            var dir = Path.Combine(root, "skins", name);
            var file = Path.Combine(dir, "skin.xaml");
            var text = File.ReadAllText(file);
            var check = AllowedUris.Aggregate(text, (t, u) => t.Replace(u, ""));
            if (check.Contains("://")) throw new InvalidDataException("skin " + name + " references an external URI");
            foreach (var t in DeniedTokens)
                if (Regex.IsMatch(check, @"\b" + Regex.Escape(t) + @"\b"))
                    throw new InvalidDataException("skin " + name + " uses forbidden '" + t + "'");

            var ctx = new ParserContext { BaseUri = new Uri(dir + "\\") };
            var el = XamlReader.Parse(text, ctx) as FrameworkElement;
            if (el == null) throw new InvalidDataException("skin " + name + " root is not a FrameworkElement");

            if (motionBoard != null) motionBoard.Stop(skinRoot);
            if (stateBoard != null) stateBoard.Stop(skinRoot);
            skinRoot = el;
            stateBoard = null;
            skinBox.Child = el;
            motionBoard = PrepareBoard(el.Resources["Motion"] as Storyboard);
            if (motionBoard != null) motionBoard.Begin(el, true);
            skinFile = file;
            skinStamp = File.GetLastWriteTimeUtc(file);
            saved.Skin = name;
            var state = currentState ?? "Idle";
            currentState = null;
            SetState(state);
        }

        void SetState(string state, bool restart = false)
        {
            if (state == currentState && !restart) return;
            if (stateBoard != null) { stateBoard.Stop(skinRoot); stateBoard = null; }
            currentState = state;
            var sb = PrepareBoard(skinRoot.Resources[state] as Storyboard);
            if (sb != null) { sb.Begin(skinRoot, true); stateBoard = sb; }
            if (motionBoard != null) motionBoard.SetSpeedRatio(skinRoot, cfg.Speeds[state]);
        }

        // ------------------------------------------------------------ agent state and events
        static string AgentTitle(Dictionary<string, object> agent)
        {
            var t = Json.Str(agent, "terminal_title_stripped");
            if (string.IsNullOrEmpty(t)) t = Json.Str(agent, "name");
            if (string.IsNullOrEmpty(t)) t = Json.Str(agent, "agent") ?? "agent";
            // OMP panes are titled "π > <session>"
            return Regex.Replace(t, "^π\\s*>\\s*", "").Trim();
        }

        void AddEvent(string from, string message, string tab)
        {
            newsTicker.Hide();
            history.Insert(0, string.Format("{0:HH:mm} {1}: {2}", DateTime.Now, from, message));
            if (history.Count > 20) history.RemoveAt(20);
            if (tab != null) lastPingTab = tab;
            var ping = skinRoot == null ? null : PrepareBoard(skinRoot.Resources["Ping"] as Storyboard);
            if (ping != null) ping.Begin(skinRoot, HandoffBehavior.Compose);
            if (!saved.Quiet)
            {
                bubbleText.Text = from + ": " + message;
                bubble.IsOpen = false;
                bubble.IsOpen = true;
                bubbleUntil = BubbleExpiry();
            }
        }

        void UpdateAgents(object[] agents)
        {
            var now = DateTime.Now;
            var next = new Dictionary<string, Pane>();
            foreach (var o in agents)
            {
                var a = Json.Object(o);
                var id = Json.Str(a, "pane_id");
                if (id == null) continue;
                var status = Json.Str(a, "agent_status") ?? "unknown";
                Pane prev;
                panes.TryGetValue(id, out prev);
                var session = Json.Str(a, "agent_session");
                var sameSession = prev != null && prev.AgentSession == session;
                var completion = Json.Get(a, "completion_seq");
                var pane = new Pane
                {
                    Status = status,
                    Title = AgentTitle(a),
                    Tab = Json.Str(a, "tab_id"),
                    Changed = prev != null && prev.Status == status ? prev.Changed : now,
                    Focused = Json.Bool(a, "focused", false),
                    AgentSession = session,
                    // herdr omits the marker while working; retain the last one for deduplication.
                    CompletionSeq = completion != null ? (long?)Convert.ToInt64(completion)
                        : sameSession ? prev.CompletionSeq : null,
                };
                next[id] = pane;
                // Completion markers survive focused auto-acknowledgement and avoid treating idle as done.
                if (seenAgents && sameSession && pane.Focused && completion != null
                    && (!prev.CompletionSeq.HasValue || pane.CompletionSeq > prev.CompletionSeq))
                {
                    focusedDoneUntil = now.AddSeconds(2);
                    focusedDoneRestart = true;
                }
                if (seenAgents && prev != null && prev.Status != status && !pane.Focused)
                {
                    if (status == "done") AddEvent(pane.Title, "done", null);
                    else if (status == "blocked") AddEvent(pane.Title, "needs input", null);
                }
            }
            panes = next;
            seenAgents = true;
        }

        string DerivedState()
        {
            if (overrideState != null && DateTime.Now < overrideUntil) return overrideState;
            var statuses = panes.Values.Select(p => p.Status).ToList();
            if (statuses.Contains("blocked")) return "Attention";
            if (DateTime.Now < gameFlashUntil) return gameFlash;
            if (DateTime.Now < focusedDoneUntil) return "Done";
            if (statuses.Contains("working")) return "Busy";
            if (panes.Values.Any(p => p.Status == "done" && !p.Focused)) return "Done";
            return "Idle";
        }

        void UpdateToolTip()
        {
            if (!HoverStatusToolTipEnabled) return;
            var lines = new List<string>();
            if (herdrError != null) lines.Add("herdr unreachable: " + herdrError);
            foreach (var p in panes.Values.OrderBy(p => p.Title)) lines.Add(string.Format("{0,-9} {1}", p.Status, p.Title));
            if (history.Count > 0)
            {
                lines.Add("");
                lines.AddRange(history.Take(5));
            }
            skinBox.ToolTip = lines.Count > 0 ? string.Join("\n", lines) : "No agents";
        }

        string ClickTab()
        {
            foreach (var status in new[] { "blocked", "done" })
            {
                var hit = panes.Values.Where(p => p.Status == status && p.Tab != null)
                    .OrderByDescending(p => p.Changed).FirstOrDefault();
                if (hit != null) return hit.Tab;
            }
            return lastPingTab;
        }

        void Click()
        {
            bubble.IsOpen = false;
            var tab = ClickTab();
            if (tab != null && herdrExe != null)
            {
                try
                {
                    using (Process.Start(new ProcessStartInfo(herdrExe, "tab focus " + tab) { UseShellExecute = false, CreateNoWindow = true })) { }
                }
                catch (Exception ex) { Log("herdr tab focus: " + ex.Message); }
            }
            var wins = Win32.TerminalWindows();
            var pick = wins.FirstOrDefault(h => Regex.IsMatch(Win32.Title(h), cfg.TerminalTitleRegex));
            if (pick == IntPtr.Zero && wins.Count > 0) pick = wins[0];
            if (pick != IntPtr.Zero)
            {
                if (Win32.IsIconic(pick)) Win32.ShowWindow(pick, 9);
                Win32.SetForegroundWindow(pick);
            }
        }

        void DrainInbox()
        {
            InboxItem item;
            while (inbox.TryDequeue(out item))
            {
                switch (item.Kind)
                {
                    case "hn":
                        if (item.Generation != newsGeneration || !saved.HnNews) break;
                        if (item.Error != null) newsFailures = Math.Min(4, newsFailures + 1);
                        else newsFailures = 0;
                        if (item.Stories != null) newsTicker.Add(item.Stories);
                        nextNews = DateTime.UtcNow.AddSeconds(newsRandom.Next(60, 121) * Math.Max(1, newsFailures * 2));
                        break;
                    case "agents":
                        if (item.Agents == null) herdrError = item.Error;
                        else { herdrError = null; UpdateAgents(item.Agents); }
                        break;
                    case "ping":
                        if (item.State != null)
                        {
                            overrideState = StateNames[item.State];
                            overrideUntil = DateTime.Now.AddSeconds(cfg.OverrideSeconds);
                        }
                        AddEvent(item.From, item.Msg, item.Tab);
                        Reveal();
                        break;
                    case "fault":
                        Log(item.Msg);
                        AddEvent("adjutant", item.Msg, null);
                        break;
                }
            }
        }

        void SaveState()
        {
            saved.Left = window.Left;
            saved.Top = window.Top;
            saved.Size = window.Width;
            saved.Save(statePath);
            dirty = false;
        }

        void CancelNews()
        {
            if (newsCancel == null) return;
            try { newsCancel.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        void ResetNews()
        {
            newsGeneration++;
            CancelNews();
            newsTicker.Clear();
            newsFailures = 0;
            // First opt-in gets a quick preview; subsequent editions arrive every 1–2 minutes.
            nextNews = saved.HnNews ? DateTime.UtcNow.AddSeconds(newsRandom.Next(2, 6)) : DateTime.MaxValue;
        }

        bool NewsMayShow()
        {
            return saved.HnNews && !saved.Quiet && herdrError == null
                && (currentState == "Idle" || currentState == "Busy")
                && !bubble.IsOpen && !menu.IsOpen && DateTime.Now >= revealUntil
                && !games.Any(g => g.IsOpen);
        }

        void TickNews()
        {
            var allowed = NewsMayShow();
            newsTicker.SetSide(saved.NewsSide);
            newsTicker.SetVisible(allowed, saved.Dim);
            if (!allowed || DateTime.UtcNow < nextNews || Volatile.Read(ref newsFetching) != 0) return;
            if (Interlocked.CompareExchange(ref newsFetching, 1, 0) != 0) return;
            var generation = newsGeneration;
            var cancel = new CancellationTokenSource();
            newsCancel = cancel;
            cancel.CancelAfter(12000);
            nextNews = DateTime.MaxValue;
            ThreadPool.QueueUserWorkItem(o =>
            {
                var result = new InboxItem { Kind = "hn", Generation = generation };
                try { result.Stories = newsFeed.Fetch(cancel.Token); }
                catch (Exception) { result.Error = "offline"; }
                finally
                {
                    cancel.Dispose();
                    inbox.Enqueue(result);
                    Interlocked.Exchange(ref newsFetching, 0);
                }
            });
        }

        void Tick()
        {
            if (mouseRoutingError) { mouseRoutingError = false; Log("right-click routing failed; normal mouse routing restored"); }
            DrainInbox();
            var state = DerivedState();
            SetState(state, (focusedDoneRestart && state == "Done") || (gameFlashRestart && state == gameFlash));
            focusedDoneRestart = gameFlashRestart = false;
            var blocked = panes.Values.Any(p => p.Status == "blocked");
            foreach (var g in games) g.SetPaused(blocked);
            if (bubble.IsOpen && DateTime.Now > bubbleUntil) bubble.IsOpen = false;
            var terminalInFront = Win32.TerminalInFront();
            if (bubble.IsOpen && bubbleUntil == DateTime.MaxValue && terminalInFront && !terminalWasInFront) bubble.IsOpen = false;
            terminalWasInFront = terminalInFront;
            TickNews();
            if (revealUntil != DateTime.MinValue && DateTime.Now >= revealUntil)
            {
                revealUntil = DateTime.MinValue;
                FadeToRestIfIdle();
            }
            ticks++;
            if (ticks % 5 == 0)
            {
                UpdateToolTip();
                var stamp = File.GetLastWriteTimeUtc(skinFile);
                if (stamp != skinStamp)
                {
                    skinStamp = stamp;
                    try { ImportSkin(saved.Skin); AddEvent("adjutant", "reloaded skin " + saved.Skin, null); }
                    catch (Exception ex) { AddEvent("adjutant", "skin reload failed: " + ex.Message, null); }
                }
            }
            if (ticks % 10 == 0 && dirty) SaveState();
        }

        void Run()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) => { Log("unhandled: " + e.Exception.Message); e.Handled = true; };

            BuildWindow();
            try { ImportSkin(saved.Skin); }
            catch (Exception ex)
            {
                Log("skin " + saved.Skin + " failed, falling back to atom: " + ex.Message);
                ImportSkin("atom");
            }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (s, e) =>
            {
                try { Tick(); }
                catch (Exception ex) { Log("tick: " + ex.Message); }
            };
            window.Closed += (s, e) =>
            {
                if (mouseHook != IntPtr.Zero) { Win32.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
                timer.Stop();
                newsTicker.Hide();
                CancelNews();
                running = false;
                if (listener != null) { try { listener.Stop(); } catch (Exception) { } }
                try { SaveState(); }
                catch (Exception ex) { Log("save on exit: " + ex.Message); }
            };

            StartThread(HttpLoop);
            StartThread(HerdrLoop);
            ResetNews();
            timer.Start();
            Log(string.Format("started pid={0} port={1} skin={2}", Process.GetCurrentProcess().Id, cfg.Port, saved.Skin));
            app.Run(window);
            Log("stopped");
        }
    }
}
