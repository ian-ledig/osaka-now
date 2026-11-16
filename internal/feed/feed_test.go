package feed

import (
	"strings"
	"testing"
	"time"
)

var monday = time.Date(2026, 11, 16, 0, 0, 0, 0, time.UTC)

const page = `<script type="application/ld+json">[{"@type":"Event","name":"御堂筋イルミネーション","startDate":"2026-11-21",
"endDate":"2026-11-21","location":{"name":"御堂筋","address":{"addressLocality":"大阪市中央区"}},"url":"https://example.com/"},
{"@type":"BreadcrumbList"}]</script>`

func TestParseEvents(t *testing.T) {
	list := ParseEvents(page, 0, false)
	if len(list) != 1 || list[0].Place != "御堂筋 · 大阪市中央区" || list[0].URL != "https://example.com/" {
		t.Fatalf("got %+v", list)
	}
	// The sites sometimes put raw line breaks inside JSON strings.
	list = ParseEvents("<script type=\"application/ld+json\">{\"@type\":\"Event\",\"name\":\"a\nb\",\"startDate\":\"2026-11-21\"}</script>", 0, false)
	if len(list) != 1 || list[0].Name != "a b" {
		t.Fatalf("line break inside a string: got %+v", list)
	}
}

func TestRate(t *testing.T) {
	date := ParseEvents(page, 0, false)[0]
	kids := Entry{Name: "親子で学ぶセミナー", Start: "2026-11-17", End: "2026-11-17"}
	if !Rate(&date, monday) || !Rate(&kids, monday) {
		t.Fatal("both events are within the window")
	}
	if date.Score <= kids.Score {
		t.Errorf("a weekend illumination (%v) should beat a kids' seminar tomorrow (%v)", date.Score, kids.Score)
	}
	if date.Tag != "Illumination" || date.When != "Sat 21 Nov" || date.Badge != Red {
		t.Errorf("labels: %q %q %q", date.Tag, date.When, date.Badge)
	}
	if kids.When != "Tmrw 17 Nov" {
		t.Errorf("tomorrow: %q", kids.When)
	}

	running := Entry{Name: "x", Start: "2026-11-01", End: "2026-11-23"}
	if !Rate(&running, monday) || running.When != "Now → 23 Nov" || running.Badge != Green {
		t.Errorf("running event: %+v", running)
	}

	for _, e := range []Entry{
		{Name: "over", Start: "2026-11-01", End: "2026-11-15"},
		{Name: "too far", Start: "2027-02-01", End: "2027-02-02"},
		{Name: "no date", Start: "soon"},
	} {
		if Rate(&e, monday) {
			t.Errorf("%s should be dropped", e.Name)
		}
	}
}

func TestMerge(t *testing.T) {
	all := Merge(ParseEvents(page, 0, false), []Entry{{Name: "大阪・光の饗宴「御堂筋イルミネーション」", Start: "2026-11-21"}})
	if len(all) != 1 || all[0].Pop != 3 {
		t.Fatalf("the same event from two sites should merge: %+v", all)
	}
	all = Merge(all, []Entry{{Name: "別のイベント", Start: "2026-11-21"}})
	if len(all) != 2 {
		t.Fatalf("a different event should be kept: %+v", all)
	}
}

func TestDecode(t *testing.T) {
	sjis := []byte{0x91, 0xE5, 0x8D, 0xE3} // 大阪
	if got := decode(sjis, "text/html;charset=Windows-31J"); got != "大阪" {
		t.Errorf("Shift_JIS: %q", got)
	}
	if got := decode([]byte("大阪"), "text/html; charset=utf-8"); got != "大阪" {
		t.Errorf("UTF-8: %q", got)
	}
}

func TestParseReleases(t *testing.T) {
	list := ParseReleases(`<small class="time">11月6日公開</small><h2 class="title"><a href="/movie/1/">GROTESQQQUE グロテスク</a></h2>`+
		`<small class="time">12月30日公開</small><h2 class="title"><a href="/movie/2/">A &amp; B</a></h2>`,
		time.Date(2027, 1, 5, 0, 0, 0, 0, time.UTC))
	want := []Release{{"grotesqqqueグロテスク", "2026-11-06"}, {"ab", "2026-12-30"}}
	if len(list) != 2 || list[0] != want[0] || list[1] != want[1] {
		t.Fatalf("got %+v", list)
	}
}

func TestSameTitle(t *testing.T) {
	for _, c := range []struct {
		toho, eiga string
		same       bool
	}{
		{"ＧＲＯＴＥＳＱＱＱＵＥ　－グロテスク－", "GROTESQQQUE グロテスク", true},
		{"南極料理人　４Ｋリマスター", "南極料理人", true},
		{"ボス", "BOSS ボス", false}, // too short to match by "contains"
	} {
		if got := sameTitle(key(c.toho), key(c.eiga)); got != c.same {
			t.Errorf("%s / %s: got %v", c.toho, c.eiga, got)
		}
	}
}

func TestRankMovies(t *testing.T) {
	list := RankMovies([]Entry{{Name: "old"}, {Name: "oct", Start: "2026-10-09"}, {Name: "new", Start: "2026-11-13"}}, monday)
	got := list[0].Name + "/" + list[0].When + ", " + list[1].Name + "/" + list[1].When + ", " + list[2].Name + "/" + list[2].When
	if got != "new/New 13 Nov, oct/Since 9 Oct, old/Now showing" {
		t.Fatal(got)
	}
}

const times = `{"data":[{"code":"029202","name":"ゴジラ（字幕）","list":[{"code":"037","list":[{"date":"20261117","list":[
{"code":6,"showingStart":"19:10","showingEnd":"21:45","screen":{"name":"本館スクリーン３","theaterCd":"0371","code":"13"},"unsoldSeatInfo":{"unsoldSeatStatus":"D"}},
{"code":3,"showingStart":"9:45","showingEnd":"12:20","screen":{"name":"本館スクリーン３","theaterCd":"0371","code":"13"},"unsoldSeatInfo":{"unsoldSeatStatus":"A"}}
]}]}]}]}`

func TestParseTimes(t *testing.T) {
	rows, err := ParseTimes(times, "037", "20261117", monday)
	if err != nil || len(rows) != 2 {
		t.Fatalf("rows=%d err=%v", len(rows), err)
	}
	if rows[0].Name != "9:45 – 12:20" || rows[0].Tag != "本館スクリーン3" || rows[0].Badge != Green {
		t.Errorf("the morning showing should come first: %+v", rows[0])
	}
	if !strings.Contains(rows[0].URL, "jyoei_date=20261117&gekijyo_cd=0371&screen_cd=13&sakuhin_cd=029202&pf_no=3") {
		t.Errorf("seat selection link: %q", rows[0].URL)
	}
	if rows[1].When != "Sold out" || rows[1].URL != "" {
		t.Errorf("a sold-out showing has no link: %+v", rows[1])
	}

	// Codes that are not digits must never reach a URL.
	rows, _ = ParseTimes(strings.Replace(times, `"0371"`, `"03/../71"`, -1), "037", "20261117", monday)
	for _, r := range rows {
		if r.URL != "" {
			t.Errorf("unsafe code ended up in a link: %q", r.URL)
		}
	}
}

func TestLinks(t *testing.T) {
	for address, ok := range map[string]bool{
		"https://a.jp/x": true, "http://a.jp/x": true,
		"file:///c:/x.exe": false, "javascript:1": false, "calc.exe": false, "": false,
	} {
		if IsWeb(address) != ok {
			t.Errorf("IsWeb(%q) should be %v", address, ok)
		}
	}
	for address, ok := range map[string]bool{
		"https://ms-cache.walkerplus.com/a.jpg": true, "https://www.jalan.net/a.jpg": true,
		"https://hlo.tohotheater.jp/images_net/movie/1/a.jpg": true,
		"https://evil-jalan.net/a.jpg":                        false, "http://192.168.0.1/a.jpg": false, "": false,
	} {
		if IsImage(address) != ok {
			t.Errorf("IsImage(%q) should be %v", address, ok)
		}
	}
}
