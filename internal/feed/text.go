package feed

import (
	"encoding/json"
	"regexp"
	"strconv"
	"strings"
	"time"

	"golang.org/x/text/width"
)

var (
	notWord = regexp.MustCompile(`[^\p{L}\p{N}]+`)
	control = regexp.MustCompile(`[\x00-\x1F]`)
	digits  = regexp.MustCompile(`^\d+$`)
)

// fold turns the ＦＵＬＬ－ＷＩＤＴＨ letters and digits TOHO uses into plain ones.
func fold(s string) string { return strings.TrimSpace(width.Fold.String(s)) }

func squash(s string) string { return notWord.ReplaceAllString(s, "") }

// key reduces a title to letters and digits, for comparing titles across sites.
func key(title string) string { return strings.ToLower(squash(fold(title))) }

// parseJSON is forgiving about one thing the sites get wrong: raw line breaks
// inside strings.
func parseJSON(s string) (any, error) {
	var v any
	err := json.Unmarshal([]byte(control.ReplaceAllString(s, " ")), &v)
	return v, err
}

// str, kid and kids read loosely typed JSON without panicking on the wrong shape.
func str(v any, name string) string {
	m, _ := v.(map[string]any)
	switch x := m[name].(type) {
	case string:
		return strings.TrimSpace(x)
	case float64:
		return strconv.FormatFloat(x, 'f', -1, 64)
	}
	return ""
}

func kid(v any, name string) any {
	m, _ := v.(map[string]any)
	return m[name]
}

func kids(v any, name string) []any {
	list, _ := kid(v, name).([]any)
	return list
}

// Today is the local date as a UTC midnight, so that differences between
// dates are whole days whatever the time zone.
func Today() time.Time {
	y, m, d := time.Now().Date()
	return time.Date(y, m, d, 0, 0, 0, 0, time.UTC)
}

func date(s string) (time.Time, bool) {
	if len(s) < 10 {
		return time.Time{}, false
	}
	t, err := time.Parse("2006-01-02", s[:10])
	return t, err == nil
}

func days(from, to time.Time) int { return int(to.Sub(from).Hours() / 24) }
