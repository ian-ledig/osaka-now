// Osaka Now: events and TOHO Cinemas showtimes around Osaka, in the Windows system tray.
// Build: build.cmd (C# 5 / .NET Framework 4.x, no dependencies). Checks: OsakaNow.exe --selftest
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

[assembly: System.Reflection.AssemblyTitle("Osaka Now")]
[assembly: System.Reflection.AssemblyProduct("Osaka Now")]
[assembly: System.Reflection.AssemblyDescription("Events and TOHO Cinemas showtimes around Osaka, in the system tray")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

// One row of the list: an event, a movie, a showtime or a plain message.
public class Ev
{
    public string Name, Start, End, Place, Url, Desc, Img, Code; // Code: TOHO movie code (movies only)
    public int Pop;      // bonus from source ranking / appearing in several lists
    public bool Sakai;
    [ScriptIgnore] public double Score;
    [ScriptIgnore] public string Tag = "", When1 = "", When2 = "";
    [ScriptIgnore] public int Pill;   // badge colour: 0 red, 1 green, 2 grey
    [ScriptIgnore] public bool Slim;  // showtime or message row: no picture
    public override string ToString() { return (When1 + " " + When2).Trim() + ", " + Name + ", " + Tag + ", " + Place; } // screen readers
}

public class Cache
{
    public string Fetched;
    public List<Ev> Events, Movies;
    public Dictionary<string, string> Released; // movie code -> yyyy-MM-dd, or "" when no date was found
}

public class Times
{
    public List<string> Days = new List<string>(); // yyyyMMdd days with showings
    public string Day;
    public List<Ev> Rows = new List<Ev>();
}

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

    // Movies: TOHO's own now-showing list and schedule API; release dates from eiga.com (TOHO publishes none).
    public static readonly string[][] Theaters = { new[] { "037", "Umeda" }, new[] { "032", "Namba" } };
    const string TohoList = "https://hlo.tohotheater.jp/data_net/json/movie/TNPI3090.JSON",
        TohoImg = "https://hlo.tohotheater.jp/images_net/movie/",
        TohoDetail = "https://hlo.tohotheater.jp/net/movie/TNPI3060J01.do?sakuhin_cd=",
        TohoApi = "https://api2.tohotheater.jp/api/schedule/",
        TohoBuy = "https://hlo.tohotheater.jp/net/ticket/",
        EigaList = "https://eiga.com/now/all/release/"; // every film now showing in Japan, newest release first, 20 per page
    const int ReleasePages = 10; // about three months of releases; older films simply sort last
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
        e.Pill = d <= 0 ? 1 : 0;
        if (d <= 0) { e.When1 = "Now"; e.When2 = left == 0 ? "ends today" : "→ " + en.ToString("d MMM", Inv); }
        else { e.When1 = d == 1 ? "Tmrw" : s.ToString("ddd", Inv); e.When2 = s.ToString("d MMM", Inv); }
        return true;
    }

    public static List<Ev> Rank(IEnumerable<Ev> events, DateTime today)
    {
        return events.Where(e => Rate(e, today)).OrderByDescending(e => e.Score).Take(MaxShown).ToList();
    }

    // Newest release first; films without a known date keep TOHO's order at the end.
    public static List<Ev> RankMovies(IEnumerable<Ev> movies, DateTime today)
    {
        var list = movies.ToList();
        foreach (var e in list)
        {
            DateTime s;
            e.Tag = e.Desc ?? "";
            if (!D(e.Start, out s)) { e.When1 = "Now"; e.When2 = "showing"; e.Pill = 2; e.Score = 0; continue; }
            int days = (today - s).Days;
            e.When1 = days < 0 ? "From" : days <= 7 ? "New" : "Since";
            e.When2 = s.ToString("d MMM", Inv);
            e.Pill = days <= 7 ? 0 : 2;
            e.Score = s.Ticks;
        }
        return list.OrderByDescending(e => e.Score).Take(MaxShown).ToList();
    }

    static string Str(Dictionary<string, object> d, string key)
    {
        object v;
        return d != null && d.TryGetValue(key, out v) && v != null ? v.ToString().Trim() : "";
    }

    static Dictionary<string, object> Kid(Dictionary<string, object> d, string key)
    {
        object v;
        return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
    }

    static IEnumerable<Dictionary<string, object>> Kids(Dictionary<string, object> d, string key)
    {
        object v;
        var a = d != null && d.TryGetValue(key, out v) ? v as object[] : null;
        return (a ?? new object[0]).OfType<Dictionary<string, object>>();
    }

    static Dictionary<string, object> Json(string s)
    {
        return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(s) as Dictionary<string, object>;
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
                var loc = Kid(d, "location");
                string place = string.Join(" · ", new[] { Str(loc, "name"), Str(Kid(loc, "address"), "addressLocality") }.Where(x => x != ""));
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
            charset = (resp.CharacterSet ?? "").ToLowerInvariant();
            return ms.ToArray();
        }
    }

    static string Get(string url)
    {
        string cs;
        byte[] data = GetBytes(url, out cs);
        Encoding enc = Encoding.UTF8; // also for "no charset given", which .NET reports as latin-1
        try { if (cs.Contains("31j")) enc = Encoding.GetEncoding(932); else if (cs != "" && cs != "iso-8859-1") enc = Encoding.GetEncoding(cs); } // jalan says Windows-31J
        catch { }
        return enc.GetString(data);
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

    // ---- movies ----------------------------------------------------------------------------

    static string Nice(string s) { return s.Normalize(NormalizationForm.FormKC).Trim(); } // TOHO writes ＦＵＬＬ－ＷＩＤＴＨ latin

    public static string Key(string title) { return Norm(Nice(title)).ToLowerInvariant(); }

    public static bool SameTitle(string a, string b) // keys; "contains" covers suffixes like 4Kリマスター
    {
        return a == b || (Math.Min(a.Length, b.Length) >= 5 && (a.Contains(b) || b.Contains(a)));
    }

    // eiga.com list page -> (title key, release date). Dates there have no year.
    public static List<Tuple<string, string>> ParseReleases(string html, DateTime today)
    {
        var list = new List<Tuple<string, string>>();
        foreach (Match m in Regex.Matches(html, "<small class=\"time\">(?:(\\d{4})年)?(\\d{1,2})月(\\d{1,2})日.*?<h2 class=\"title\"><a[^>]*>(.*?)</a>", RegexOptions.Singleline))
        {
            DateTime d;
            string y = m.Groups[1].Success ? m.Groups[1].Value : today.Year.ToString();
            if (!DateTime.TryParseExact(y + "-" + m.Groups[2].Value + "-" + m.Groups[3].Value, "yyyy-M-d", Inv, DateTimeStyles.None, out d)) continue;
            if (!m.Groups[1].Success && d > today.AddDays(60)) d = d.AddYears(-1); // "12月25日" seen in January was last year
            list.Add(Tuple.Create(Key(WebUtility.HtmlDecode(m.Groups[4].Value)), d.ToString("yyyy-MM-dd")));
        }
        return list;
    }

    // released: persistent movie code -> date, so eiga.com is only asked about films not seen before.
    public static List<Ev> FetchMovies(Dictionary<string, string> released, DateTime today, Action<string> log)
    {
        var data = Kids(Json(Get(TohoList)), "data").Where(d => Regex.IsMatch(Str(d, "mcode"), @"^\d+$") && Str(d, "name") != "").ToList();
        var unknown = data.Where(d => !released.ContainsKey(Str(d, "mcode"))).ToList();
        bool complete = true;
        for (int p = 1; p <= ReleasePages && unknown.Count > 0; p++)
        {
            try
            {
                foreach (var r in ParseReleases(Get(EigaList + (p == 1 ? "" : p + "/")), today))
                    foreach (var d in unknown.Where(d => SameTitle(Key(Str(d, "name")), r.Item1)).ToList())
                    {
                        released[Str(d, "mcode")] = r.Item2;
                        unknown.Remove(d);
                    }
            }
            catch (Exception ex) { complete = false; if (log != null) log("FAIL\teiga page " + p + "\t" + ex.Message); }
            Thread.Sleep(1000); // be polite to the site
        }
        if (complete) foreach (var d in unknown) released[Str(d, "mcode")] = ""; // re-run or older film: don't ask again
        foreach (var k in released.Keys.Where(k => !data.Any(d => Str(d, "mcode") == k)).ToList()) released.Remove(k); // no longer showing
        if (log != null) log(data.Count + "\t" + TohoList + "\t(" + released.Values.Count(v => v != "") + " with a release date)");

        return data.Select(d =>
        {
            string code = Str(d, "mcode"), img = Str(d, "sakuhinGazouNm");
            return new Ev
            {
                Code = code, Name = Nice(Str(d, "name")), Start = released.ContainsKey(code) ? released[code] : "",
                Desc = Nice(Str(d, "kantokuNm")), Place = Nice(Str(d, "syutuenSyaNm")), Url = TohoDetail + code,
                Img = img == "" ? "" : TohoImg + code + "/" + Uri.EscapeDataString(img)
            };
        }).ToList();
    }

    // One row per showing, earliest first. Clicking a row with seats opens TOHO's seat selection for it.
    public static List<Ev> ParseTimes(string json, string vg, string day)
    {
        var rows = new List<Ev>();
        string closed = string.CompareOrdinal(day, DateTime.Today.ToString("yyyyMMdd")) > 0 ? "Not on sale yet" : "Sales closed";
        foreach (var m in Kids(Json(json), "data")) // film variants (subtitled, IMAX...), then theater, day, showing
            foreach (var th in Kids(m, "list"))
                foreach (var dd in Kids(th, "list"))
                    foreach (var s in Kids(dd, "list"))
                    {
                        var scr = Kid(s, "screen");
                        string st = Str(Kid(s, "unsoldSeatInfo"), "unsoldSeatStatus");
                        var e = new Ev
                        {
                            Slim = true, Name = Str(s, "showingStart") + " – " + Str(s, "showingEnd"), Tag = Nice(Str(scr, "name")), Place = Nice(Str(m, "name")),
                            Start = Str(s, "showingStart").PadLeft(5, '0'), // sort key: "9:20" must come before "13:30"
                            When1 = st == "A" ? "Plenty of seats" : st == "B" ? "Seats available" : st == "C" ? "Few seats left"
                                  : st == "D" ? "Sold out" : st == "G" ? closed : "See website",
                            Pill = st == "A" || st == "B" ? 1 : st == "C" ? 0 : 2
                        };
                        string[] ids = { vg, day, Str(scr, "theaterCd"), Str(scr, "code"), Str(m, "code"), Str(s, "code") };
                        if (e.Pill != 2 && ids.All(x => Regex.IsMatch(x, @"^\d+$"))) // scraped values go into a URL: digits only
                            e.Url = TohoBuy + vg + "/TNPI2040J03.do?site_cd=" + vg + "&jyoei_date=" + day + "&gekijyo_cd=" + ids[2] + "&screen_cd=" + ids[3]
                                  + "&sakuhin_cd=" + ids[4] + "&pf_no=" + ids[5] + "&fnc=1&pageid=2000J01&enter_kbn=";
                        rows.Add(e);
                    }
        return rows.OrderBy(e => e.Start, StringComparer.Ordinal).ToList();
    }

    // days == null: also ask which days have showings; day == null: take the first of them.
    public static Times Showtimes(string code, string vg, string day, List<string> days)
    {
        if (!Regex.IsMatch(code + vg + (day ?? "0"), @"^\d+$")) throw new ArgumentException("bad code");
        string q = "?__type__=json&movie_cd=" + code + "&vg_cd=" + vg + "&site_cd=&enter_kbn=";
        var t = new Times { Day = day };
        t.Days = days ?? Kids(Json(Get(TohoApi + "v1/schedule/TNPI3070J02" + q + "&term=99&seq_disp_term=7")), "data")
            .Where(d => Str(d, "selectable") == "1" && Regex.IsMatch(Str(d, "date"), @"^\d{8}$")).Select(d => Str(d, "date")).ToList();
        if (t.Day == null) t.Day = t.Days.FirstOrDefault();
        if (t.Day != null) t.Rows = ParseTimes(Get(TohoApi + "v2/schedule/TNPI3070J04" + q + "&show_day=" + t.Day + "&isMember="), vg, t.Day);
        return t;
    }

    // ---- pictures and links ----------------------------------------------------------------

    public static bool SafeUrl(string url)
    {
        Uri u;
        return Uri.TryCreate(url, UriKind.Absolute, out u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
    }

    // Image URLs come from scraped pages: only fetch http(s) from the source sites' own hosts.
    public static bool ImageOk(string url)
    {
        if (!SafeUrl(url)) return false;
        string h = new Uri(url).Host;
        return new[] { "walkerplus.com", "jalan.net", "tohotheater.jp" }.Any(d => h == d || h.EndsWith("." + d));
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
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    float k = Math.Max((float)w / (r - l), (float)h / (bot - top)), sw = w / k, sh = h / k;
                    g.DrawImage(src, new RectangleF(0, 0, w, h), new RectangleF(l + (r - l - sw) / 2, top + (bot - top - sh) / 2, sw, sh), GraphicsUnit.Pixel);
                }
                if (!cached) { Directory.CreateDirectory(dir); t.Save(file, ImageFormat.Jpeg); } // cache the small thumb, not the original
                return t;
            }
        }
        catch { if (cached) File.Delete(file); throw; } // a corrupt cache file is re-downloaded next time
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

        var rel = ParseReleases("<small class=\"time\">11月6日公開</small><h2 class=\"title\"><a href=\"/movie/1/\">GROTESQQQUE グロテスク</a></h2>" +
            "<small class=\"time\">12月30日公開</small><h2 class=\"title\"><a href=\"/movie/2/\">A &amp; B</a></h2>", new DateTime(2027, 1, 5));
        ok(rel.Count == 2 && rel[0].Item2 == "2026-11-06" && rel[1].Item2 == "2026-12-30" && rel[1].Item1 == "ab", "release dates and year guess");
        ok(SameTitle(Key("ＧＲＯＴＥＳＱＱＱＵＥ　－グロテスク－"), rel[0].Item1) && SameTitle(Key("南極料理人　４Ｋリマスター"), Key("南極料理人"))
            && !SameTitle(Key("ボス"), Key("BOSS ボス")), "title match");
        var mv = RankMovies(new[] { new Ev { Name = "old", Start = "" }, new Ev { Name = "oct", Start = "2026-10-09" }, new Ev { Name = "new", Start = "2026-11-13" } }, today);
        ok(mv[0].Name == "new" && mv[0].When1 == "New" && mv[1].When1 == "Since" && mv[2].Name == "old", "movies newest first");

        var rows = ParseTimes("{\"data\":[{\"code\":\"029202\",\"name\":\"ゴジラ（字幕）\",\"list\":[{\"code\":\"037\",\"list\":[{\"date\":\"20261117\",\"list\":[" +
            "{\"code\":6,\"showingStart\":\"19:10\",\"showingEnd\":\"21:45\",\"screen\":{\"name\":\"本館スクリーン３\",\"theaterCd\":\"0371\",\"code\":\"13\"},\"unsoldSeatInfo\":{\"unsoldSeatStatus\":\"D\"}}," +
            "{\"code\":3,\"showingStart\":\"9:45\",\"showingEnd\":\"12:20\",\"screen\":{\"name\":\"本館スクリーン３\",\"theaterCd\":\"0371\",\"code\":\"13\"},\"unsoldSeatInfo\":{\"unsoldSeatStatus\":\"A\"}}" +
            "]}]}]}]}", "037", "20261117");
        ok(rows.Count == 2 && rows[0].Name == "9:45 – 12:20" && rows[0].Tag == "本館スクリーン3" && rows[0].Pill == 1
            && rows[0].Url.Contains("jyoei_date=20261117&gekijyo_cd=0371&screen_cd=13&sakuhin_cd=029202&pf_no=3")
            && rows[1].When1 == "Sold out" && rows[1].Url == null, "showtimes, morning first");
        Console.WriteLine("selftest ok");
        return 0;
    }
}

class Flyout : Form
{
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x80; return p; } } // tool window: not in Alt+Tab
}

class FlatList : ListBox
{
    protected override void WndProc(ref Message m)
    {
        // Rows repaint themselves completely, so erasing the background first only causes flicker.
        if (m.Msg == 0x14 && Items.Count * ItemHeight >= ClientSize.Height) { m.Result = (IntPtr)1; return; }
        base.WndProc(ref m);
    }
}

class App : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "OsakaNow";
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsakaNow");
    static readonly string CachePath = Path.Combine(DataDir, "cache.json"), ThumbDir = Path.Combine(DataDir, "thumbs");
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Color Red = Color.FromArgb(201, 59, 50), Green = Color.FromArgb(27, 127, 76), Gray = Color.FromArgb(104, 104, 104); // all >= 4.5:1 under white text
    const TextFormatFlags Tf = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsTranslateTransform;

    // Yu Gothic UI ships with Windows 10/11 and covers both Latin and Japanese.
    readonly Font font = new Font("Yu Gothic UI", 9f), small = new Font("Yu Gothic UI Semibold", 8.25f),
                  title = new Font("Yu Gothic UI Semibold", 10.5f), head = new Font("Yu Gothic UI Semibold", 14f);
    readonly NotifyIcon tray = new NotifyIcon();
    readonly Form popup = new Flyout();
    readonly Panel header = new Panel(), bar = new Panel();
    readonly ListBox list = new FlatList();
    readonly Label barTitle = new Label();
    readonly Button back = new Button();
    readonly FlowLayoutPanel dayRow;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Dictionary<string, Bitmap> thumbs = new Dictionary<string, Bitmap>(); // by image url; null = failed, don't retry this run
    int pad, thumbW, rowH, slimH;
    Color bg, hover, fg, dim, fgSel, dimSel; // set by ApplyTheme

    List<Ev> events = new List<Ev>(), movies = new List<Ev>();
    Dictionary<string, string> released = new Dictionary<string, string>();
    DateTime fetched = DateTime.MinValue, hiddenAt;
    bool busy, loadingThumbs, placeholder;
    string subtitle = "";
    int tab;                 // 0 events, 1 movies
    Ev showing;              // movie whose showtimes are on screen; null = normal list
    string theater = Core.Theaters[0][0];
    List<string> days;       // days with showings for `showing` at `theater`
    int seq, backTop;        // seq: ignore answers to requests the user has moved on from

    public App()
    {
        using (var g = popup.CreateGraphics()) pad = (int)(8 * g.DpiX / 96);
        thumbW = pad * 14;
        rowH = pad * 12;
        slimH = pad * 10;
        int chipH = title.Height + pad * 2;

        popup.FormBorderStyle = FormBorderStyle.None;
        popup.ShowInTaskbar = false;
        popup.TopMost = true;
        popup.StartPosition = FormStartPosition.Manual;
        popup.ClientSize = new Size(pad * 62, rowH * 8);
        popup.Padding = new Padding(0, 0, 0, pad);
        popup.KeyPreview = true;
        popup.KeyDown += (s, e) =>
        {
            if (showing != null && (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Back)) Back();
            else if (e.KeyCode == Keys.Escape) popup.Hide();
        };
        popup.Deactivate += (s, e) => { popup.Hide(); hiddenAt = DateTime.UtcNow; };
        popup.FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; popup.Hide(); } };
        int w = popup.ClientSize.Width;

        // Header: title, status line, tabs.
        header.Dock = DockStyle.Top;
        header.Height = pad * 2 + head.Height + font.Height + chipH;
        header.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int d = head.Height / 2, x = pad * 2, y = pad * 3 / 2;
            using (var b = new SolidBrush(Red)) e.Graphics.FillEllipse(b, x, y + (head.Height - d) / 2, d, d);
            TextRenderer.DrawText(e.Graphics, "Osaka Now", head, new Point(x + d + pad, y), fg, Tf);
            TextRenderer.DrawText(e.Graphics, subtitle, font, new Point(x, y + head.Height + pad / 4), dim, Tf);
        };
        var tabRow = Row(header, chipH);
        tabRow.Dock = DockStyle.Bottom;
        Chip(tabRow, "Events", true, () => { tab = 0; UpdateUi(); });
        Chip(tabRow, "Movies", false, () => { tab = 1; UpdateUi(); });

        // Showtimes bar (only while a movie is open): back + title, theater, day.
        bar.Dock = DockStyle.Top;
        bar.Visible = false;
        back.Text = "←  Back";
        back.AccessibleName = "Back to the movie list";
        back.FlatStyle = FlatStyle.Flat;
        back.FlatAppearance.BorderSize = 0;
        back.Font = font;
        back.AutoSize = true;
        back.Cursor = Cursors.Hand;
        back.Location = new Point(pad * 3 / 2, pad / 2);
        back.Click += (s, e) => Back();
        bar.Controls.Add(back);
        barTitle.Font = title;
        barTitle.AutoEllipsis = true;
        barTitle.UseMnemonic = false;
        barTitle.TextAlign = ContentAlignment.MiddleLeft;
        barTitle.SetBounds(back.Right + pad, back.Top, w - back.Right - pad * 3, back.Height);
        bar.Controls.Add(barTitle);
        var theaterRow = Row(bar, chipH);
        theaterRow.SetBounds(0, back.Bottom + pad / 2, w, chipH);
        foreach (var t in Core.Theaters)
        {
            string code = t[0];
            Chip(theaterRow, t[1], code == theater, () => { theater = code; LoadTimes(null); });
        }
        dayRow = Row(bar, chipH);
        dayRow.SetBounds(0, theaterRow.Bottom, w, chipH);
        bar.Height = dayRow.Bottom + pad / 2;

        list.Dock = DockStyle.Fill;
        list.BorderStyle = BorderStyle.None;
        list.IntegralHeight = false;
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.ItemHeight = rowH;
        list.DrawItem += (s, e) =>
        {
            if (e.Index < 0) return;
            using (var buf = BufferedGraphicsManager.Current.Allocate(e.Graphics, e.Bounds)) // paint off-screen, show in one go: no flicker
            {
                DrawRow(buf.Graphics, (Ev)list.Items[e.Index], e.Bounds, (e.State & DrawItemState.Selected) != 0);
                buf.Render();
            }
        };
        list.MouseMove += (s, e) => { int i = list.IndexFromPoint(e.Location); if (i >= 0 && i != list.SelectedIndex) list.SelectedIndex = i; };
        list.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenSelected(); };
        list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) OpenSelected(); };

        popup.Controls.Add(list);   // fill control first, so the bars dock above it
        popup.Controls.Add(bar);
        popup.Controls.Add(header);
        int round = 2;
        DwmSetWindowAttribute(popup.Handle, 33, ref round, 4); // Windows 11: rounded corners + shadow. Fails harmlessly on Windows 10.
        ApplyTheme();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh now", null, (s, e) => Refresh(true));
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
            movies = c.Movies ?? movies;
            released = c.Released ?? released;
            fetched = DateTime.Parse(c.Fetched, Inv, DateTimeStyles.RoundtripKind);
        }
        catch { } // no or broken cache: just fetch
        UpdateUi();

        // Cheap 30-min check instead of one 6-hour timer, so sleep/resume and failed fetches catch up.
        timer.Interval = 15000; // first check shortly after startup, without slowing the boot
        timer.Tick += (s, e) => { timer.Interval = 30 * 60 * 1000; Refresh(false); };
        timer.Start();
    }

    // ---- small UI builders -----------------------------------------------------------------

    FlowLayoutPanel Row(Control parent, int height)
    {
        var f = new FlowLayoutPanel { WrapContents = false, Height = height, Padding = new Padding(pad * 3 / 2, 0, 0, 0) };
        parent.Controls.Add(f);
        return f;
    }

    // A flat radio button: real control, so tabs/theaters/days work with keyboard and screen readers.
    void Chip(Control parent, string text, bool on, Action picked)
    {
        var rb = new RadioButton
        {
            Text = text, Checked = on, Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, AutoSize = true, Font = title,
            UseMnemonic = false, Cursor = Cursors.Hand, Margin = new Padding(0, 0, pad / 2, 0)
        };
        rb.FlatAppearance.BorderSize = 0;
        rb.Paint += (s, e) => { if (rb.Checked) using (var b = new SolidBrush(Red)) e.Graphics.FillRectangle(b, pad / 2, rb.Height - 3, rb.Width - pad, 3); };
        rb.CheckedChanged += (s, e) => { Tint(rb); if (rb.Checked) { picked(); list.Focus(); } };
        parent.Controls.Add(rb);
        Tint(rb);
    }

    void Tint(RadioButton rb)
    {
        rb.BackColor = rb.FlatAppearance.CheckedBackColor = bg;
        rb.FlatAppearance.MouseOverBackColor = rb.FlatAppearance.MouseDownBackColor = hover;
        rb.ForeColor = rb.Checked ? fg : dim;
    }

    static IEnumerable<Control> All(Control c) { return c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(All(x))); }

    static Ev Msg(string text) { return new Ev { Name = text, Slim = true }; }

    void SetRows(IEnumerable<Ev> rows)
    {
        list.BeginUpdate();
        list.Items.Clear();
        list.Items.AddRange(rows.ToArray());
        list.EndUpdate();
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
        popup.BackColor = list.BackColor = bg; // panels, labels and buttons inherit from the form
        popup.ForeColor = fg;
        back.FlatAppearance.MouseOverBackColor = back.FlatAppearance.MouseDownBackColor = hover;
        foreach (var rb in All(popup).OfType<RadioButton>()) Tint(rb);
        SetWindowTheme(list.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(popup.Handle, 20, ref v, 4);
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

    void DrawRow(Graphics g, Ev ev, Rectangle r, bool sel) // sel: hovered or keyboard-selected
    {
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, r); // before antialiasing, or the row edges show as faint lines
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (sel)
            using (var b = new SolidBrush(hover))
            using (var p = Round(Rectangle.Inflate(r, -pad, -pad / 4), pad)) g.FillPath(b, p);

        var tr = new Rectangle(r.X + pad * 2, r.Y + pad, ev.Slim ? 0 : thumbW, r.Height - pad * 2);
        if (!ev.Slim)
            using (var p = Round(tr, pad * 3 / 4))
            {
                Bitmap img;
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
        int x = ev.Slim ? tr.X + pad / 2 : tr.Right + pad * 3 / 2, w = r.Right - x - pad * 2, gap = pad / 2, pillH = small.Height + pad / 2;
        string when = (ev.When1 + " " + ev.When2).Trim(), place = ev.Place ?? "";
        var wrap = Tf | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl; // title: up to two lines, then ellipsis
        int th = Math.Min(TextRenderer.MeasureText(g, ev.Name, title, new Size(w, 9999), wrap).Height, title.Height * 2);
        int y = tr.Y + (tr.Height - th - (when != "" ? pillH + gap : 0) - (place != "" ? font.Height + gap : 0)) / 2;
        TextRenderer.DrawText(g, ev.Name, title, new Rectangle(x, y, w, th), cf, wrap);
        y += th + gap;
        if (when != "")
        {
            var pill = new Rectangle(x, y, TextRenderer.MeasureText(g, when, small, new Size(9999, 9999), TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width + pad * 2, pillH);
            using (var b = new SolidBrush(ev.Pill == 1 ? Green : ev.Pill == 2 ? Gray : Red))
            using (var p = Round(pill, pillH / 2)) g.FillPath(b, p);
            TextRenderer.DrawText(g, when, small, pill, Color.White, Tf | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, ev.Tag ?? "", font, new Rectangle(pill.Right + pad, y, r.Right - pill.Right - pad * 3, pillH), cd, Tf | TextFormatFlags.VerticalCenter);
            y += pillH + gap;
        }
        TextRenderer.DrawText(g, place, font, new Rectangle(x, y, w, font.Height), cd, Tf);
    }

    // ---- data ------------------------------------------------------------------------------

    void Refresh(bool force)
    {
        bool evDue = force || (DateTime.Now - fetched).TotalHours >= Core.RefreshHours;
        if (busy || !(evDue || movies.Count == 0)) return; // movies.Count == 0: first run after an update, or TOHO failed last time
        busy = true;
        tray.Text = "Osaka Now: updating…";
        subtitle = "Updating…";
        header.Invalidate();
        Task.Run(() =>
        {
            var ev = evDue ? Core.FetchAll(null) : new List<Ev>();
            List<Ev> mv = null;
            try { mv = Core.FetchMovies(released, DateTime.Today, null); } catch { } // keep the old movie list
            return Tuple.Create(ev, mv);
        }).ContinueWith(t =>
        {
            busy = false;
            bool ok = t.Status == TaskStatus.RanToCompletion;
            if (ok && t.Result.Item1.Count > 0) { events = t.Result.Item1; fetched = DateTime.Now; }
            if (ok && t.Result.Item2 != null && t.Result.Item2.Count > 0) movies = t.Result.Item2;
            if (ok && fetched != DateTime.MinValue)
            {
                try
                {
                    Directory.CreateDirectory(DataDir);
                    File.WriteAllText(CachePath, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                        .Serialize(new Cache { Fetched = fetched.ToString("o"), Events = events, Movies = movies, Released = released }));
                }
                catch { } // cache is only an optimisation
                try
                {
                    foreach (var f in new DirectoryInfo(ThumbDir).GetFiles().Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30))) f.Delete();
                }
                catch { } // no thumbs yet, or one is in use
            }
            // Never reshuffle a list someone is reading: new data shows the next time the window opens.
            if (popup.Visible && (showing != null || !placeholder)) Caption(); else UpdateUi();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Caption()
    {
        string when = fetched == DateTime.MinValue ? "no data yet" : "updated " + fetched.ToString("ddd HH:mm", Inv);
        tray.Text = "Osaka Now (" + when + ")";
        popup.Text = "Osaka Now"; // for screen readers; the visible title is painted in the header
        subtitle = (tab == 0 ? "Coming up for two" : "Now at TOHO Cinemas, newest first") + "  ·  " + when;
        header.Invalidate();
    }

    // Rebuilds the list for the current tab (and leaves the showtimes view).
    void UpdateUi()
    {
        showing = null;
        seq++;
        bar.Visible = false;
        list.ItemHeight = rowH;
        Caption();
        var today = DateTime.Today; // re-rank each time: scores depend on today's date
        List<Ev> ev = Core.Rank(events, today), mv = Core.RankMovies(movies, today), rows = tab == 0 ? ev : mv;
        placeholder = rows.Count == 0;
        SetRows(placeholder ? new List<Ev> { Msg(busy || fetched == DateTime.MinValue ? "Loading…" : "Nothing to show right now.") } : rows);

        // Keep the thumbnails of both tabs in memory (so switching is instant), and load the missing ones in the background.
        var all = ev.Concat(mv).ToList();
        foreach (var k in thumbs.Keys.Where(k => !all.Any(e => e.Img == k)).ToList())
        {
            if (thumbs[k] != null) thumbs[k].Dispose();
            thumbs.Remove(k);
        }
        var want = all.Select(e => e.Img).Where(u => Core.ImageOk(u) && !thumbs.ContainsKey(u)).Distinct().ToList();
        if (loadingThumbs || want.Count == 0) return; // anything missed is picked up the next time the list opens
        loadingThumbs = true;
        int w = thumbW, h = rowH - pad * 2;
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

    // ---- showtimes -------------------------------------------------------------------------

    void ShowTimes(Ev movie)
    {
        showing = movie;
        backTop = list.TopIndex;
        barTitle.Text = movie.Name;
        bar.Visible = true;
        list.ItemHeight = slimH;
        LoadTimes(null);
    }

    // day == null: (re)load the days for the current theater and show the first one.
    void LoadTimes(string day)
    {
        if (showing == null) return;
        int my = ++seq;
        string code = showing.Code, vg = theater;
        var known = day == null ? null : days;
        SetRows(new[] { Msg("Loading showtimes…") });
        Task.Run(() => Core.Showtimes(code, vg, day, known)).ContinueWith(t =>
        {
            if (my != seq) return; // the user picked something else meanwhile
            if (t.Status != TaskStatus.RanToCompletion) { SetRows(new[] { Msg("Could not load showtimes. Check the connection and pick the theater again.") }); return; }
            var r = t.Result;
            if (day == null)
            {
                days = r.Days;
                dayRow.SuspendLayout();
                while (dayRow.Controls.Count > 0) dayRow.Controls[0].Dispose();
                foreach (var d in days)
                {
                    string dd = d;
                    var date = DateTime.ParseExact(d, "yyyyMMdd", Inv);
                    Chip(dayRow, date == DateTime.Today ? "Today" : date.ToString("ddd d", Inv), d == r.Day, () => LoadTimes(dd));
                }
                dayRow.ResumeLayout();
            }
            SetRows(r.Rows.Count > 0 ? r.Rows : new List<Ev> { Msg(r.Day == null ? "No showings here in the coming days." : "No showings on this day.") });
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Back()
    {
        int top = backTop;
        UpdateUi();
        if (top < list.Items.Count) list.TopIndex = top; // back to where the user was in the movie list
        list.Focus();
    }

    // ---- window ----------------------------------------------------------------------------

    void Toggle()
    {
        if (popup.Visible) { popup.Hide(); return; }
        if ((DateTime.UtcNow - hiddenAt).TotalMilliseconds < 300) return; // the tray click itself just closed it
        ApplyTheme();
        UpdateUi();
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        popup.Height = wa.Height - 16;
        popup.Location = new Point(wa.Right - popup.Width - 8, wa.Bottom - popup.Height - 8);
        popup.Show();
        popup.Activate();
        list.Focus();
    }

    void OpenSelected()
    {
        var ev = list.SelectedItem as Ev;
        if (ev == null) return;
        if (showing == null && !string.IsNullOrEmpty(ev.Code)) ShowTimes(ev);
        else if (Core.SafeUrl(ev.Url)) Process.Start(ev.Url); // scraped data: only ever hand http(s) to the shell
    }

    static Icon MakeIcon()
    {
        using (var b = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(b))
        using (var red = new SolidBrush(Color.FromArgb(222, 54, 64)))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
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
        if (args.Contains("--dump")) // fetch once and print both rankings, for tuning
        {
            // a windowless exe has no console to configure, so wrap the redirected stdout instead
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            foreach (var e in Core.Rank(Core.FetchAll(Console.WriteLine), DateTime.Today))
                Console.WriteLine(e.Score.ToString("0") + "\t" + e.When1 + " " + e.When2 + "\t" + e.Tag + "\t" + e.Name + "\t" + e.Place + "\t" + e.Url);
            foreach (var e in Core.RankMovies(Core.FetchMovies(new Dictionary<string, string>(), DateTime.Today, Console.WriteLine), DateTime.Today))
                Console.WriteLine(e.When1 + " " + e.When2 + "\t" + e.Name + "\t" + e.Tag + "\t" + e.Url);
            return 0;
        }
        bool first;
        using (new Mutex(true, "OsakaNow.SingleInstance", out first))
        {
            if (!first) return 0;
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.Run(new App());
        }
        return 0;
    }
}
