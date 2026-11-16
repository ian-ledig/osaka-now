// Package feed fetches and ranks what the app shows: events around Osaka,
// films at TOHO Cinemas and their showtimes.
package feed

// Badge is the colour of the small label on a row.
type Badge string

const (
	Red   Badge = "red"
	Green Badge = "green"
	Grey  Badge = "grey"
)

// Entry is one row of a list: an event, a film or a showing.
type Entry struct {
	Name  string `json:"name"`
	Start string `json:"start"` // 2006-01-02. A showing keeps its start time here, zero-padded, as a sort key.
	End   string `json:"end"`
	Place string `json:"place"` // films: cast. Showings: which version of the film.
	URL   string `json:"url"`
	Desc  string `json:"desc"` // films: director
	Img   string `json:"img"`
	Code  string `json:"code"` // TOHO film code
	Pop   int    `json:"pop"`  // bonus from the source's own ranking, or from being listed several times
	Sakai bool   `json:"sakai"`

	// Filled in when a list is ranked.
	Score float64 `json:"score"`
	Tag   string  `json:"tag"`
	When  string  `json:"when"`
	Badge Badge   `json:"badge"`
}

// Showtimes is what the window needs for one film at one theater.
type Showtimes struct {
	Days []string `json:"days"` // 20060102, the days that have showings
	Day  string   `json:"day"`
	Rows []Entry  `json:"rows"`
}
