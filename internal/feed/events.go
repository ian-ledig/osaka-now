package feed

import (
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"
)

var jsonLD = regexp.MustCompile(`(?s)<script type="application/ld\+json">(.*?)</script>`)

// ParseEvents reads the schema.org events embedded in a listing page.
func ParseEvents(html string, pop int, sakai bool) []Entry {
	var list []Entry
	for _, m := range jsonLD.FindAllStringSubmatch(html, -1) {
		root, err := parseJSON(m[1])
		if err != nil {
			continue
		}
		items, ok := root.([]any)
		if !ok {
			items = []any{root}
		}
		for _, d := range items {
			if str(d, "@type") != "Event" || str(d, "name") == "" {
				continue
			}
			loc := kid(d, "location")
			var parts []string
			for _, p := range []string{str(loc, "name"), str(kid(loc, "address"), "addressLocality")} {
				if p != "" {
					parts = append(parts, p)
				}
			}
			place := strings.Join(parts, " · ")
			if sakai && !strings.Contains(place, "堺") { // Jalan gives only the neighbourhood
				place = strings.TrimRight("堺市 · "+place, " ·")
			}
			list = append(list, Entry{
				Name: str(d, "name"), Start: str(d, "startDate"), End: str(d, "endDate"), Place: place, URL: str(d, "url"),
				Desc: str(d, "description"), Img: str(d, "image"), Pop: pop, Sakai: sakai || strings.Contains(place, "堺"),
			})
		}
	}
	return list
}

// Merge adds events to all. The same start date and one name containing the
// other means the same event listed twice.
func Merge(all, add []Entry) []Entry {
next:
	for _, e := range add {
		k := squash(e.Name)
		for i := range all {
			n := squash(all[i].Name)
			if all[i].Start != e.Start || !(n == k || (min(len([]rune(n)), len([]rune(k))) >= 6 && (strings.Contains(n, k) || strings.Contains(k, n)))) {
				continue
			}
			all[i].Pop = min(15, max(all[i].Pop, e.Pop)+3) // listed several times = more notable
			if e.Sakai && !all[i].Sakai {
				all[i].Place = e.Place // the Sakai listing names the city
			}
			all[i].Sakai = all[i].Sakai || e.Sakai
			continue next
		}
		all = append(all, e)
	}
	return all
}

// Rate scores an event and sets its labels. It returns false when the event
// is over or too far away.
func Rate(e *Entry, today time.Time) bool {
	start, ok := date(e.Start)
	if !ok {
		return false
	}
	end, ok := date(e.End)
	if !ok || end.Before(start) {
		end = start
	}
	until, left, length := days(today, start), days(today, end), days(start, end)+1
	if left < 0 || until > HorizonDays {
		return false
	}

	var timing float64
	switch {
	case until > 14:
		timing = 15 - float64(until-14)/3
	case until > 0:
		timing = float64(45 - 2*until)
	case length <= 3: // short and on now
		timing = 50
	case left <= 7: // last chance
		timing = 40
	default: // long-running: don't let it sit at the top for months
		timing = 25 - min(15, float64(length)/10)
	}

	weekend := false // a short event that covers the coming Saturday or Sunday
	for i := max(until, 0); i <= min(left, 6) && length <= 14; i++ {
		if d := today.AddDate(0, 0, i).Weekday(); d == time.Saturday || d == time.Sunday {
			weekend = true
		}
	}

	text := e.Name + " " + e.Desc
	fit, hits := 0, 0
	e.Tag = ""
	for _, l := range Likes {
		if !containsAny(text, l.Words) {
			continue
		}
		if hits == 0 {
			fit, e.Tag = l.Weight, l.Tag
		} else if hits <= 2 {
			fit += 4
		}
		hits++
	}
	dislikes := 0
	for _, w := range Dislikes {
		if strings.Contains(text, w) {
			dislikes++
		}
	}
	fit -= min(dislikes*25, 40)

	e.Score = timing + float64(fit+e.Pop)
	if weekend {
		e.Score += 8
	}
	if e.Sakai {
		e.Score += 6
	}
	switch {
	case until > 1:
		e.Badge, e.When = Red, start.Format("Mon 2 Jan")
	case until == 1:
		e.Badge, e.When = Red, "Tmrw "+start.Format("2 Jan")
	case left == 0:
		e.Badge, e.When = Green, "Now ends today"
	default:
		e.Badge, e.When = Green, "Now → "+end.Format("2 Jan")
	}
	return true
}

func containsAny(text string, words []string) bool {
	for _, w := range words {
		if strings.Contains(text, w) {
			return true
		}
	}
	return false
}

// RankEvents returns the events worth showing today, best first.
func RankEvents(events []Entry, today time.Time) []Entry {
	ranked := make([]Entry, 0, len(events))
	for _, e := range events {
		if Rate(&e, today) {
			ranked = append(ranked, e)
		}
	}
	sort.SliceStable(ranked, func(i, j int) bool { return ranked[i].Score > ranked[j].Score })
	return ranked[:min(len(ranked), MaxShown)]
}

// FetchEvents reads every source. A source that fails is skipped and
// reported through log.
func FetchEvents(log func(string)) []Entry {
	var all []Entry
	for _, src := range Sources {
		html, err := getText(src.URL)
		if err != nil {
			log("FAIL\t" + err.Error())
		} else {
			got := ParseEvents(html, src.Pop, src.Sakai)
			all = Merge(all, got)
			log(strconv.Itoa(len(got)) + "\t" + src.URL)
		}
		time.Sleep(pause) // don't hammer the sites
	}
	return all
}
