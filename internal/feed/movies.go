package feed

import (
	"errors"
	"fmt"
	"html"
	"net/url"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"
)

// Films come from TOHO's own now-showing feed and schedule service. Release
// dates come from eiga.com, because TOHO publishes none.
const (
	tohoList     = "https://hlo.tohotheater.jp/data_net/json/movie/TNPI3090.JSON"
	tohoImages   = "https://hlo.tohotheater.jp/images_net/movie/"
	tohoDetail   = "https://hlo.tohotheater.jp/net/movie/TNPI3060J01.do?sakuhin_cd="
	tohoSchedule = "https://api2.tohotheater.jp/api/schedule/"
	tohoTickets  = "https://hlo.tohotheater.jp/net/ticket/"
	eigaReleases = "https://eiga.com/now/all/release/"
)

var release = regexp.MustCompile(`(?s)<small class="time">(?:(\d{4})年)?(\d{1,2})月(\d{1,2})日.*?<h2 class="title"><a[^>]*>(.*?)</a>`)

// sameTitle compares two title keys. "Contains" covers suffixes such as
// 4Kリマスター that only one of the sites adds.
func sameTitle(a, b string) bool {
	return a == b || (min(len([]rune(a)), len([]rune(b))) >= 5 && (strings.Contains(a, b) || strings.Contains(b, a)))
}

// Release is a film title key with its release date.
type Release struct{ Key, Date string }

// ParseReleases reads an eiga.com list page. Dates there have no year.
func ParseReleases(page string, today time.Time) []Release {
	var list []Release
	for _, m := range release.FindAllStringSubmatch(page, -1) {
		year := today.Year()
		if m[1] != "" {
			year, _ = strconv.Atoi(m[1])
		}
		month, _ := strconv.Atoi(m[2])
		day, _ := strconv.Atoi(m[3])
		d := time.Date(year, time.Month(month), day, 0, 0, 0, 0, time.UTC)
		if d.Day() != day { // "2月30日" and the like
			continue
		}
		if m[1] == "" && days(today, d) > 60 { // "12月25日" seen in January was last year
			d = d.AddDate(-1, 0, 0)
		}
		list = append(list, Release{key(html.UnescapeString(m[4])), d.Format("2006-01-02")})
	}
	return list
}

// FetchMovies returns the films now showing. released maps a film code to its
// release date and is kept between runs, so eiga.com is only asked about films
// that have not been seen before.
func FetchMovies(released map[string]string, today time.Time, log func(string)) ([]Entry, error) {
	text, err := getText(tohoList)
	if err != nil {
		return nil, err
	}
	root, err := parseJSON(text)
	if err != nil {
		return nil, err
	}
	var films, unknown []any
	showing := map[string]bool{}
	for _, d := range kids(root, "data") {
		code := str(d, "mcode")
		if !digits.MatchString(code) || str(d, "name") == "" {
			continue
		}
		films = append(films, d)
		showing[code] = true
		if _, known := released[code]; !known {
			unknown = append(unknown, d)
		}
	}

	complete := true
	for page := 1; page <= releasePages && len(unknown) > 0; page++ {
		address := eigaReleases
		if page > 1 {
			address += strconv.Itoa(page) + "/"
		}
		if list, err := getText(address); err != nil {
			complete = false
			log("FAIL\t" + err.Error())
		} else {
			for _, r := range ParseReleases(list, today) {
				rest := unknown[:0]
				for _, d := range unknown {
					if sameTitle(key(str(d, "name")), r.Key) {
						released[str(d, "mcode")] = r.Date
					} else {
						rest = append(rest, d)
					}
				}
				unknown = rest
			}
		}
		time.Sleep(pause)
	}
	if complete {
		for _, d := range unknown {
			released[str(d, "mcode")] = "" // a re-run or an older film: don't ask again
		}
	}
	dated := 0
	for code, d := range released {
		if !showing[code] {
			delete(released, code)
		} else if d != "" {
			dated++
		}
	}
	log(fmt.Sprintf("%d\t%s\t(%d with a release date)", len(films), tohoList, dated))

	list := make([]Entry, 0, len(films))
	for _, d := range films {
		code := str(d, "mcode")
		e := Entry{
			Code: code, Name: fold(str(d, "name")), Start: released[code], Desc: fold(str(d, "kantokuNm")),
			Place: fold(str(d, "syutuenSyaNm")), URL: tohoDetail + code,
		}
		if img := str(d, "sakuhinGazouNm"); img != "" {
			e.Img = tohoImages + code + "/" + url.PathEscape(img)
		}
		list = append(list, e)
	}
	return list, nil
}

// RankMovies sorts newest release first. Films without a known date keep
// TOHO's order at the end.
func RankMovies(movies []Entry, today time.Time) []Entry {
	ranked := make([]Entry, len(movies))
	copy(ranked, movies)
	for i := range ranked {
		e := &ranked[i]
		e.Tag = e.Desc
		start, ok := date(e.Start)
		if !ok {
			e.When, e.Badge, e.Score = "Now showing", Grey, 0
			continue
		}
		e.Badge, e.Score = Grey, float64(start.Unix())
		switch age := days(start, today); {
		case age < 0:
			e.Badge, e.When = Red, "From "+start.Format("2 Jan")
		case age <= 7:
			e.Badge, e.When = Red, "New "+start.Format("2 Jan")
		default:
			e.When = "Since " + start.Format("2 Jan")
		}
	}
	sort.SliceStable(ranked, func(i, j int) bool { return ranked[i].Score > ranked[j].Score })
	return ranked[:min(len(ranked), MaxShown)]
}

// ParseTimes returns one row per showing, earliest first. A row with seats
// links to TOHO's seat selection for that showing.
func ParseTimes(text, theater, day string, today time.Time) ([]Entry, error) {
	root, err := parseJSON(text)
	if err != nil {
		return nil, err
	}
	closed := "Sales closed"
	if day > today.Format("20060102") {
		closed = "Not on sale yet"
	}
	var rows []Entry
	for _, film := range kids(root, "data") { // the film and its versions (subtitled, IMAX...)
		for _, th := range kids(film, "list") {
			for _, dd := range kids(th, "list") {
				for _, s := range kids(dd, "list") {
					screen := kid(s, "screen")
					e := Entry{
						Name:  str(s, "showingStart") + " – " + str(s, "showingEnd"),
						Start: fmt.Sprintf("%05s", str(s, "showingStart")), // "9:20" has to sort before "13:30"
						Tag:   fold(str(screen, "name")), Place: fold(str(film, "name")), Badge: Grey,
					}
					switch str(kid(s, "unsoldSeatInfo"), "unsoldSeatStatus") {
					case "A":
						e.When, e.Badge = "Plenty of seats", Green
					case "B":
						e.When, e.Badge = "Seats available", Green
					case "C":
						e.When, e.Badge = "Few seats left", Red
					case "D":
						e.When = "Sold out"
					case "G":
						e.When = closed
					default:
						e.When = "See website"
					}
					// These values come from the web and go into a URL: digits only.
					ids := []string{theater, day, str(screen, "theaterCd"), str(screen, "code"), str(film, "code"), str(s, "code")}
					if e.Badge != Grey && allDigits(ids) {
						e.URL = fmt.Sprintf("%s%s/TNPI2040J03.do?site_cd=%s&jyoei_date=%s&gekijyo_cd=%s&screen_cd=%s&sakuhin_cd=%s&pf_no=%s&fnc=1&pageid=2000J01&enter_kbn=",
							tohoTickets, theater, theater, day, ids[2], ids[3], ids[4], ids[5])
					}
					rows = append(rows, e)
				}
			}
		}
	}
	sort.SliceStable(rows, func(i, j int) bool { return rows[i].Start < rows[j].Start })
	return rows, nil
}

func allDigits(ids []string) bool {
	for _, id := range ids {
		if !digits.MatchString(id) {
			return false
		}
	}
	return true
}

// FetchShowtimes asks TOHO for a film's showings at one theater. With no
// days it also asks which days have showings; with no day it takes the first.
func FetchShowtimes(code, theater, day string, known []string) (Showtimes, error) {
	result := Showtimes{Days: known, Day: day, Rows: []Entry{}}
	if !allDigits([]string{code, theater}) || (day != "" && !digits.MatchString(day)) {
		return result, errors.New("bad film, theater or day code")
	}
	query := "?__type__=json&movie_cd=" + code + "&vg_cd=" + theater + "&site_cd=&enter_kbn="
	if known == nil {
		text, err := getText(tohoSchedule + "v1/schedule/TNPI3070J02" + query + "&term=99&seq_disp_term=7")
		if err != nil {
			return result, err
		}
		root, err := parseJSON(text)
		if err != nil {
			return result, err
		}
		result.Days = []string{}
		for _, d := range kids(root, "data") {
			if date := str(d, "date"); str(d, "selectable") == "1" && len(date) == 8 && digits.MatchString(date) {
				result.Days = append(result.Days, date)
			}
		}
	}
	if result.Day == "" && len(result.Days) > 0 {
		result.Day = result.Days[0]
	}
	if result.Day == "" {
		return result, nil
	}
	text, err := getText(tohoSchedule + "v2/schedule/TNPI3070J04" + query + "&show_day=" + result.Day + "&isMember=")
	if err != nil {
		return result, err
	}
	rows, err := ParseTimes(text, theater, result.Day, Today())
	if rows != nil {
		result.Rows = rows
	}
	return result, err
}
