// Osaka & Sakai events tray app. Build: build.cmd (C# 5 / .NET Framework 4.x, no dependencies).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

public class Ev
{
    public string Name, Start, End, Place, Url, Desc, Img;
    public int Pop;     // bonus from source ranking / appearing in several lists
    public bool Sakai;
    [ScriptIgnore] public double Score;
    [ScriptIgnore] public string Tag, When1, When2;
    public override string ToString() { return When1 + " " + When2 + ", " + Name + ", " + Place; } // screen readers
}

public class Cache { public string Fetched; public List<Ev> Events; }

class Like
{
    public string Tag; public int W; public string[] Words;
    public Like(string tag, int w, string words) { Tag = tag; W = w; Words = words.Split(' '); }
}

static class Core
{
    // ---- tune here -------------------------------------------------------------------------
    public const int RefreshHours = 6, HorizonDays = 45, MaxShown = 40;

    // url, popularity bonus, is-Sakai. Both sites embed schema.org Event JSON-LD in their lists.
    public static readonly Tuple<string, int, bool>[] Sources = {
        Tuple.Create("https://www.walkerplus.com/event_list/ar0727/", 10, false),        // Osaka pref, popular order
        Tuple.Create("https://www.walkerplus.com/event_list/ar0727/2.html", 6, false),
        Tuple.Create("https://www.walkerplus.com/event_list/ar0727/3.html", 3, false),
        Tuple.Create("https://www.walkerplus.com/event_list/weekend/ar0727/", 5, false),
        Tuple.Create("https://www.walkerplus.com/event_list/ar0727140/sakai/", 0, true),
        Tuple.Create("https://www.jalan.net/event/270000/", 4, false),                   // Osaka pref
        Tuple.Create("https://www.jalan.net/event/270000/page_2/", 2, false),
        Tuple.Create("https://www.jalan.net/event/270000/page_3/", 0, false),
        Tuple.Create("https://www.jalan.net/event/270000/page_4/", 0, false),
        Tuple.Create("https://www.jalan.net/event/maj_271400000/", 0, true),             // Sakai city
    };

    // Ordered by weight: the first match gives the tag. Matched against name + description.
    static readonly Like[] Likes = {
        new Like("Illumination", 30, "イルミネーション ライトアップ 光の ランタン キャンドル 夜景 プロジェクションマッピング"),
        new Like("Fireworks", 30, "花火"),
        new Like("Christmas", 28, "クリスマス Xmas"),
        new Like("Autumn leaves", 26, "紅葉 もみじ"),
        new Like("Flowers", 24, "桜 花見 梅まつり 梅林 バラ園 ローズガーデン 薔薇 菊 あじさい 紫陽花 藤まつり コスモス チューリップ ひまわり 庭園 フラワー"),
        new Like("Food & drink", 22, "グルメ ワイン ビール 日本酒 スイーツ チョコ ブッフェ ビュッフェ 食べ放題 アフタヌーンティー 肉 ラーメン カフェ ベーカリー"),
        new Like("Market", 20, "マルシェ マーケット 蚤の市 骨董 陶器 クラフト 手作り市 朝市"),
        new Like("Art", 20, "美術 アート 展覧会 特別展 企画展 写真展 ミュージアム 没入"),
        new Like("Music", 18, "コンサート ライブ ジャズ 音楽 フェス オーケストラ クラシック"),
        new Like("Festival", 18, "祭 まつり 縁日 盆踊り 初詣 えびす"),
        new Like("Outing", 12, "水族館 動物園 プラネタリウム クルーズ 温泉 遊園地 スケート"),
    };
    static readonly string[] Dislikes = ("子ども こども 子供 キッズ 親子 ファミリー 小学生 幼児 ベビー 赤ちゃん ヒーロー プリキュア アンパンマン " +
        "セミナー 講座 講演 説明会 相談会 就職 健康 体操 シニア 競馬 競輪 ボートレース 囲碁 将棋").Split(' ');
    // ----------------------------------------------------------------------------------------

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static bool D(string s, out DateTime d)
    {
        d = DateTime.MinValue;
        return s != null && s.Length >= 10 && DateTime.TryParseExact(s.Substring(0, 10), "yyyy-MM-dd", Inv, DateTimeStyles.None, out d);
    }

    // Sets Score/Tag/When*; false when the event is over or too far away.
    public static bool Rate(Ev e, DateTime today)
    {
        DateTime s, en;
        if (!D(e.Start, out s)) return false;
        if (!D(e.End, out en) || en < s) en = s;
        int d = (s - today).Days, left = (en - today).Days, len = (en - s).Days + 1;
        if (left < 0 || d > HorizonDays) return false;

        double time;
        if (d <= 0) time = len <= 3 ? 50 : left <= 7 ? 40 : 25 - Math.Min(15, len / 10.0); // short now > last chance > long-running
        else time = d <= 14 ? 45 - 2 * d : 15 - (d - 14) / 3.0;

        bool weekend = false; // a short event that covers the coming Sat/Sun
        for (int i = Math.Max(d, 0); i <= Math.Min(left, 6) && len <= 14; i++)
        {
            var w = today.AddDays(i).DayOfWeek;
            weekend |= w == DayOfWeek.Saturday || w == DayOfWeek.Sunday;
        }

        string text = e.Name + " " + e.Desc;
        var hits = Likes.Where(l => l.Words.Any(text.Contains)).ToList();
        e.Tag = hits.Count > 0 ? hits[0].Tag : "";
        double couple = (hits.Count > 0 ? hits[0].W + Math.Min(hits.Count - 1, 2) * 4 : 0)
                        - Math.Min(Dislikes.Count(text.Contains) * 25, 40);

        e.Score = time + couple + e.Pop + (weekend ? 8 : 0) + (e.Sakai ? 6 : 0);
        if (d <= 0) { e.When1 = "Now"; e.When2 = left == 0 ? "ends today" : "→ " + en.ToString("d MMM", Inv); }
        else { e.When1 = d == 1 ? "Tmrw" : s.ToString("ddd", Inv); e.When2 = s.ToString("d MMM", Inv); }
        return true;
    }

    public static List<Ev> Rank(IEnumerable<Ev> events, DateTime today)
    {
        return events.Where(e => Rate(e, today)).OrderByDescending(e => e.Score).Take(MaxShown).ToList();
    }

    static string Str(Dictionary<string, object> d, string key)
    {
        object v;
        return d != null && d.TryGetValue(key, out v) && v != null ? v.ToString().Trim() : "";
    }

    public static List<Ev> Parse(string html, int pop, bool sakai)
    {
        var list = new List<Ev>();
        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        foreach (Match m in Regex.Matches(html, "<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline))
        {
            object root;
            try { root = js.DeserializeObject(m.Groups[1].Value); } catch { continue; }
            foreach (var o in root as object[] ?? new[] { root })
            {
                var d = o as Dictionary<string, object>;
                if (Str(d, "@type") != "Event" || Str(d, "name") == "") continue;
                var loc = d.ContainsKey("location") ? d["location"] as Dictionary<string, object> : null;
                var adr = loc != null && loc.ContainsKey("address") ? loc["address"] as Dictionary<string, object> : null;
                string place = string.Join(" · ", new[] { Str(loc, "name"), Str(adr, "addressLocality") }.Where(x => x != ""));
                if (sakai && !place.Contains("堺")) place = ("堺市 · " + place).TrimEnd(' ', '·'); // jalan gives only the neighbourhood
                list.Add(new Ev
                {
                    Name = Str(d, "name"), Start = Str(d, "startDate"), End = Str(d, "endDate"), Place = place,
                    Url = Str(d, "url"), Desc = Str(d, "description"), Img = Str(d, "image"), Pop = pop, Sakai = sakai || place.Contains("堺")
                });
            }
        }
        return list;
    }

    static string Norm(string s) { return Regex.Replace(s, @"[\W_]+", ""); }

    // Same start date and one name containing the other = same event listed twice.
    public static void Merge(List<Ev> all, IEnumerable<Ev> add)
    {
        foreach (var e in add)
        {
            string k = Norm(e.Name);
            var dup = all.FirstOrDefault(x =>
            {
                string n = Norm(x.Name);
                return x.Start == e.Start && (n == k || (Math.Min(n.Length, k.Length) >= 6 && (n.Contains(k) || k.Contains(n))));
            });
            if (dup == null) { all.Add(e); continue; }
            dup.Pop = Math.Min(15, Math.Max(dup.Pop, e.Pop) + 3); // listed several times = more notable
            if (e.Sakai && !dup.Sakai) dup.Place = e.Place; // the Sakai listing names the city
            dup.Sakai |= e.Sakai;
        }
    }

    static byte[] GetBytes(string url, out string charset)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";
        req.Timeout = 20000;
        req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var s = resp.GetResponseStream())
        using (var ms = new MemoryStream())
        {
            var buf = new byte[81920];
            for (int n; (n = s.Read(buf, 0, buf.Length)) > 0; )
            {
                ms.Write(buf, 0, n);
                if (ms.Length > 8 << 20) throw new IOException("response too large: " + url);
            }
            charset = resp.CharacterSet ?? "";
            return ms.ToArray();
        }
    }

    static string Get(string url)
    {
        string cs;
        byte[] data = GetBytes(url, out cs);
        Encoding enc;
        try { enc = cs.ToLowerInvariant().Contains("31j") ? Encoding.GetEncoding(932) : Encoding.GetEncoding(cs); } // jalan says Windows-31J
        catch { enc = Encoding.UTF8; }
        return enc.GetString(data);
    }

    // Image URLs come from scraped pages: only fetch http(s) from the two sites' own hosts.
    public static bool ImageOk(string url)
    {
        if (!SafeUrl(url)) return false;
        string h = new Uri(url).Host;
        return new[] { "walkerplus.com", "jalan.net" }.Any(d => h == d || h.EndsWith("." + d));
    }

    // Returns a w x h centre-cropped thumbnail, from the disk cache when possible. Throws on any failure.
    public static Bitmap LoadThumb(string url, string dir, int w, int h)
    {
        string file;
        using (var md5 = System.Security.Cryptography.MD5.Create()) // just a file name, not security
            file = Path.Combine(dir, BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", "") + ".jpg");
        bool cached = File.Exists(file);
        try
        {
            byte[] data;
            if (cached) data = File.ReadAllBytes(file);
            else { string cs; data = GetBytes(url, out cs); Thread.Sleep(150); } // be polite to the sites
            using (var ms = new MemoryStream(data))
            using (var src = new Bitmap(ms))
            {
                // The sites letterbox photos with white bars: trim them (16 samples per line is enough to tell).
                Func<int, int, bool> white = (x, y) => { var c = src.GetPixel(x, y); return c.R > 240 && c.G > 240 && c.B > 240; };
                Func<int, bool> row = y => Enumerable.Range(0, 16).All(i => white(i * (src.Width - 1) / 15, y));
                Func<int, bool> col = x => Enumerable.Range(0, 16).All(i => white(x, i * (src.Height - 1) / 15));
                int l = 0, top = 0, r = src.Width, bot = src.Height;
                while (bot - top > 16 && row(top)) top++;
                while (bot - top > 16 && row(bot - 1)) bot--;
                while (r - l > 16 && col(l)) l++;
                while (r - l > 16 && col(r - 1)) r--;

                var t = new Bitmap(w, h);
                using (var g = Graphics.FromImage(t))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    float k = Math.Max((float)w / (r - l), (float)h / (bot - top)), sw = w / k, sh = h / k;
                    g.DrawImage(src, new RectangleF(0, 0, w, h), new RectangleF(l + (r - l - sw) / 2, top + (bot - top - sh) / 2, sw, sh), GraphicsUnit.Pixel);
                }
                if (!cached) { Directory.CreateDirectory(dir); t.Save(file, ImageFormat.Jpeg); } // cache the small thumb, not the original
                return t;
            }
        }
        catch { if (cached) File.Delete(file); throw; } // a corrupt cache file is re-downloaded next time
    }

    public static List<Ev> FetchAll(Action<string> log)
    {
        var all = new List<Ev>();
        foreach (var src in Sources)
        {
            try
            {
                var got = Parse(Get(src.Item1), src.Item2, src.Item3);
                Merge(all, got);
                if (log != null) log(got.Count + "\t" + src.Item1);
            }
            catch (Exception ex) { if (log != null) log("FAIL\t" + src.Item1 + "\t" + ex.Message); }
            Thread.Sleep(1000); // be polite to the sites
        }
        return all;
    }

    public static bool SafeUrl(string url)
    {
        Uri u;
        return Uri.TryCreate(url, UriKind.Absolute, out u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
    }

    public static int SelfTest()
    {
        Action<bool, string> ok = (c, what) => { if (!c) throw new Exception("selftest failed: " + what); };
        var today = new DateTime(2026, 11, 16); // a Monday
        var p = Parse("<script type=\"application/ld+json\">[{\"@type\":\"Event\",\"name\":\"御堂筋イルミネーション\",\"startDate\":\"2026-11-21\"," +
            "\"endDate\":\"2026-11-21\",\"location\":{\"name\":\"御堂筋\",\"address\":{\"addressLocality\":\"大阪市中央区\"}},\"url\":\"https://example.com/\"}," +
            "{\"@type\":\"BreadcrumbList\"}]</script>", 0, false);
        ok(p.Count == 1 && p[0].Place == "御堂筋 · 大阪市中央区" && p[0].Url == "https://example.com/", "parse");
        var kids = new Ev { Name = "親子で学ぶセミナー", Start = "2026-11-17", End = "2026-11-17", Desc = "" };
        ok(Rate(p[0], today) && Rate(kids, today) && p[0].Score > kids.Score, "couple weekend event beats kids seminar tomorrow");
        ok(p[0].Tag == "Illumination" && p[0].When1 == "Sat", "tag/label");
        ok(!Rate(new Ev { Name = "x", Start = "2026-11-01", End = "2026-11-15" }, today), "ended is dropped");
        ok(!Rate(new Ev { Name = "x", Start = "2027-02-01", End = "2027-02-02" }, today), "far future is dropped");
        var all = new List<Ev>(); Merge(all, p); Merge(all, Parse("<script type=\"application/ld+json\">{\"@type\":\"Event\"," +
            "\"name\":\"大阪・光の饗宴「御堂筋イルミネーション」\",\"startDate\":\"2026-11-21\"}</script>", 0, false));
        ok(all.Count == 1 && all[0].Pop == 3, "dedupe");
        ok(SafeUrl("https://a.jp/x") && !SafeUrl("file:///c:/x.exe") && !SafeUrl("javascript:1") && !SafeUrl("calc.exe"), "url check");
        ok(ImageOk("https://ms-cache.walkerplus.com/a.jpg") && ImageOk("https://www.jalan.net/a.jpg") && !ImageOk("https://evil-jalan.net/a.jpg")
            && !ImageOk("http://192.168.0.1/a.jpg") && !ImageOk("") && !ImageOk(null), "image host check");
        Console.WriteLine("selftest ok");
        return 0;
    }
}

class Flyout : Form
{
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x80; return p; } } // tool window: not in Alt+Tab
}

class App : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "OsakaEvents";
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsakaEvents");
    static readonly string CachePath = Path.Combine(DataDir, "cache.json"), ThumbDir = Path.Combine(DataDir, "thumbs");
    readonly Dictionary<string, Bitmap> thumbs = new Dictionary<string, Bitmap>(); // by image url; null = failed, don't retry this run
    int thumbW, thumbH;
    bool loadingThumbs;

    readonly NotifyIcon tray = new NotifyIcon();
    readonly Form popup = new Flyout();
    readonly Panel header = new Panel();
    readonly ListBox list = new ListBox();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    List<Ev> events = new List<Ev>();
    DateTime fetched = DateTime.MinValue, hiddenAt;
    bool busy;
    string subtitle = "";
    Color bg, hover, fg, dim, fgSel, dimSel; // set by ApplyTheme
    static readonly Color Red = Color.FromArgb(201, 59, 50), Green = Color.FromArgb(27, 127, 76); // both >= 4.5:1 under white text

    public App()
    {
        // Yu Gothic UI ships with Windows 10/11 and covers both Latin and Japanese.
        Font font = new Font("Yu Gothic UI", 9f), small = new Font("Yu Gothic UI Semibold", 8.25f),
             title = new Font("Yu Gothic UI Semibold", 10.5f), head = new Font("Yu Gothic UI Semibold", 14f);
        int pad;
        using (var g = popup.CreateGraphics()) pad = (int)(8 * g.DpiX / 96);
        var tf = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;

        header.Dock = DockStyle.Top;
        header.Height = pad * 3 + head.Height + font.Height;
        header.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int d = head.Height / 2, x = pad * 2, y = pad * 3 / 2;
            using (var b = new SolidBrush(Red)) e.Graphics.FillEllipse(b, x, y + (head.Height - d) / 2, d, d);
            TextRenderer.DrawText(e.Graphics, "Osaka & Sakai", head, new Point(x + d + pad, y), fg, tf);
            TextRenderer.DrawText(e.Graphics, subtitle, font, new Point(x, y + head.Height + pad / 4), dim, tf);
        };

        list.Dock = DockStyle.Fill;
        list.BorderStyle = BorderStyle.None;
        list.IntegralHeight = false;
        list.DrawMode = DrawMode.OwnerDrawFixed;
        thumbW = pad * 14; thumbH = pad * 10;
        list.ItemHeight = thumbH + pad * 2;
        list.DrawItem += (s, e) =>
        {
            if (e.Index < 0) return;
            var ev = (Ev)list.Items[e.Index];
            var g = e.Graphics;
            var r = e.Bounds;
            bool sel = (e.State & DrawItemState.Selected) != 0; // hovered or keyboard-selected row
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
            if (sel)
                using (var b = new SolidBrush(hover))
                using (var p = Round(Rectangle.Inflate(r, -pad, -pad / 4), pad)) g.FillPath(b, p);

            var tr = new Rectangle(r.X + pad * 2, r.Y + pad, thumbW, thumbH);
            Bitmap img;
            using (var p = Round(tr, pad * 3 / 4))
            {
                if (ev.Img != null && thumbs.TryGetValue(ev.Img, out img) && img != null)
                {
                    using (var tb = new TextureBrush(img, WrapMode.TileFlipXY)) // brush fill = antialiased rounded corners
                    {
                        tb.TranslateTransform(tr.X, tr.Y);
                        tb.ScaleTransform((float)tr.Width / img.Width, (float)tr.Height / img.Height);
                        g.FillPath(tb, p);
                    }
                }
                else using (var b = new SolidBrush(sel ? bg : hover)) g.FillPath(b, p); // no picture (yet): keep the rows aligned
            }

            Color cf = sel ? fgSel : fg, cd = sel ? dimSel : dim;
            int x = tr.Right + pad * 3 / 2, w = r.Right - x - pad * 2, gap = pad / 2, pillH = small.Height + pad / 2;
            var wrap = tf | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl; // title: up to two lines, then ellipsis
            int th = Math.Min(TextRenderer.MeasureText(g, ev.Name, title, new Size(w, 9999), wrap).Height, title.Height * 2);
            int y = tr.Y + (tr.Height - th - pillH - font.Height - gap * 2) / 2;
            TextRenderer.DrawText(g, ev.Name, title, new Rectangle(x, y, w, th), cf, wrap);
            y += th + gap;
            string when = ev.When1 + " " + ev.When2;
            var pill = new Rectangle(x, y, TextRenderer.MeasureText(g, when, small, new Size(9999, 9999), TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width + pad * 2, pillH);
            using (var b = new SolidBrush(ev.When1 == "Now" ? Green : Red))
            using (var p = Round(pill, pillH / 2)) g.FillPath(b, p);
            TextRenderer.DrawText(g, when, small, pill, Color.White, tf | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, ev.Tag, font, new Rectangle(pill.Right + pad, y, r.Right - pill.Right - pad * 3, pillH), cd, tf | TextFormatFlags.VerticalCenter);
            y += pillH + gap;
            TextRenderer.DrawText(g, ev.Place, font, new Rectangle(x, y, w, font.Height), cd, tf);
        };
        list.MouseMove += (s, e) => { int i = list.IndexFromPoint(e.Location); if (i >= 0 && i != list.SelectedIndex) list.SelectedIndex = i; };
        list.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenSelected(); };
        list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) OpenSelected(); else if (e.KeyCode == Keys.Escape) popup.Hide(); };

        popup.FormBorderStyle = FormBorderStyle.None;
        popup.ShowInTaskbar = false;
        popup.TopMost = true;
        popup.StartPosition = FormStartPosition.Manual;
        popup.ClientSize = new Size(pad * 62, list.ItemHeight * 8);
        popup.Padding = new Padding(0, 0, 0, pad);
        popup.Controls.Add(list);   // fill control first, so the header docks above it
        popup.Controls.Add(header);
        popup.Deactivate += (s, e) => { popup.Hide(); hiddenAt = DateTime.UtcNow; };
        popup.FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; popup.Hide(); } };
        int round = 2;
        DwmSetWindowAttribute(popup.Handle, 33, ref round, 4); // Windows 11: rounded corners + shadow. Fails harmlessly on Windows 10.
        ApplyTheme();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh now", null, (s, e) => Refresh());
        var auto = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) auto.Checked = k != null && k.GetValue(RunName) != null;
        auto.CheckedChanged += (s, e) =>
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                if (auto.Checked) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\""); else k.DeleteValue(RunName, false);
        };
        menu.Items.Add(auto);
        menu.Items.Add("Exit", null, (s, e) => { tray.Visible = false; ExitThread(); });

        tray.Icon = MakeIcon();
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Toggle(); };
        tray.Visible = true;

        try
        {
            var c = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Cache>(File.ReadAllText(CachePath));
            events = c.Events ?? events;
            fetched = DateTime.Parse(c.Fetched, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        catch { } // no or broken cache: just fetch
        UpdateUi();

        // Cheap 30-min check instead of one 6-hour timer, so sleep/resume and failed fetches catch up.
        timer.Interval = 15000; // first check shortly after startup, without slowing the boot
        timer.Tick += (s, e) =>
        {
            timer.Interval = 30 * 60 * 1000;
            if ((DateTime.Now - fetched).TotalHours >= Core.RefreshHours) Refresh();
        };
        timer.Start();
    }

    void Refresh()
    {
        if (busy) return;
        busy = true;
        tray.Text = "Osaka events: updating…";
        subtitle = "Updating…";
        header.Invalidate();
        Task.Run(() => Core.FetchAll(null)).ContinueWith(t =>
        {
            busy = false;
            if (t.Status == TaskStatus.RanToCompletion && t.Result.Count > 0)
            {
                events = t.Result;
                fetched = DateTime.Now;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                    File.WriteAllText(CachePath, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                        .Serialize(new Cache { Fetched = fetched.ToString("o"), Events = events }));
                }
                catch { } // cache is only an optimisation
                try
                {
                    foreach (var f in new DirectoryInfo(ThumbDir).GetFiles().Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30))) f.Delete();
                }
                catch { } // no thumbs yet, or one is in use
            }
            UpdateUi(); // on failure keep the old list; the timer retries in 30 min
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void UpdateUi()
    {
        string when = fetched == DateTime.MinValue ? "no data yet" : "updated " + fetched.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
        tray.Text = "Osaka & Sakai events (" + when + ")";
        popup.Text = "Osaka & Sakai: coming up"; // for screen readers; the visible title is painted in the header
        subtitle = "Coming up for two  ·  " + when;
        header.Invalidate();
        var ranked = Core.Rank(events, DateTime.Today); // re-rank each time: scores depend on today's date
        list.BeginUpdate();
        list.Items.Clear();
        list.Items.AddRange(ranked.ToArray());
        list.EndUpdate();

        // Keep only the thumbnails of listed events in memory, and load the missing ones in the background.
        foreach (var k in thumbs.Keys.Where(k => !ranked.Any(e => e.Img == k)).ToList())
        {
            if (thumbs[k] != null) thumbs[k].Dispose();
            thumbs.Remove(k);
        }
        var want = ranked.Select(e => e.Img).Where(u => Core.ImageOk(u) && !thumbs.ContainsKey(u)).Distinct().ToList();
        if (loadingThumbs || want.Count == 0) return; // anything missed is picked up the next time the list opens
        loadingThumbs = true;
        int w = thumbW, h = thumbH;
        Task.Run(() =>
        {
            foreach (var url in want)
            {
                Bitmap bmp = null;
                try { bmp = Core.LoadThumb(url, ThumbDir, w, h); } catch { }
                popup.BeginInvoke((Action)(() => { thumbs[url] = bmp; list.Invalidate(); }));
            }
        }).ContinueWith(t => { loadingThumbs = false; }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // Follows the Windows light/dark app setting; high contrast gets the system colours.
    void ApplyTheme()
    {
        bool dark = false;
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                dark = (int)k.GetValue("AppsUseLightTheme", 1) == 0;
        }
        catch { } // key missing on old Windows: stay light
        if (SystemInformation.HighContrast)
        {
            bg = SystemColors.Window; hover = SystemColors.Highlight; fg = SystemColors.WindowText; dim = SystemColors.GrayText;
            fgSel = dimSel = SystemColors.HighlightText;
        }
        else
        {
            bg = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(249, 249, 249);
            hover = dark ? Color.FromArgb(51, 51, 51) : Color.FromArgb(234, 234, 234);
            fgSel = fg = dark ? Color.White : Color.FromArgb(26, 26, 26);
            dimSel = dim = dark ? Color.FromArgb(170, 170, 170) : Color.FromArgb(92, 92, 92);
        }
        popup.BackColor = header.BackColor = list.BackColor = bg;
        SetWindowTheme(list.Handle, dark ? "DarkMode_Explorer" : "Explorer", null); // matching scrollbar
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(popup.Handle, 20, ref v, 4); // matching window border
    }

    static GraphicsPath Round(Rectangle r, int rad)
    {
        int d = rad * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    void Toggle()
    {
        if (popup.Visible) { popup.Hide(); return; }
        if ((DateTime.UtcNow - hiddenAt).TotalMilliseconds < 300) return; // the tray click itself just closed it
        ApplyTheme();
        UpdateUi();
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        popup.Height = wa.Height - 16; // almost the full screen height
        popup.Location = new Point(wa.Right - popup.Width - 8, wa.Bottom - popup.Height - 8);
        popup.Show();
        popup.Activate();
        list.Focus();
    }

    void OpenSelected()
    {
        var ev = list.SelectedItem as Ev;
        if (ev != null && Core.SafeUrl(ev.Url)) Process.Start(ev.Url); // scraped data: only ever hand http(s) to the shell
    }

    static Icon MakeIcon()
    {
        using (var b = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(b))
        using (var red = new SolidBrush(Color.FromArgb(222, 54, 64)))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(Brushes.White, 1, 1, 30, 30);
            g.FillEllipse(red, 7, 7, 18, 18);
            return Icon.FromHandle(b.GetHicon());
        }
    }

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

    [STAThread]
    static int Main(string[] args)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        if (args.Contains("--selftest")) return Core.SelfTest();
        if (args.Contains("--dump")) // fetch once and print the ranking, for tuning the keywords
        {
            // a windowless exe has no console to configure, so wrap the redirected stdout instead
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            foreach (var e in Core.Rank(Core.FetchAll(Console.WriteLine), DateTime.Today))
                Console.WriteLine(e.Score.ToString("0") + "\t" + e.When1 + " " + e.When2 + "\t" + e.Tag + "\t" + e.Name + "\t" + e.Place + "\t" + e.Url);
            return 0;
        }
        bool first;
        using (new Mutex(true, "OsakaEvents.SingleInstance", out first))
        {
            if (!first) return 0;
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.Run(new App());
        }
        return 0;
    }
}
