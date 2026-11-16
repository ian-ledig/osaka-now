package main

import (
	"maps"
	"sync"
	"time"

	"github.com/wailsapp/wails/v3/pkg/application"

	"github.com/ian-ledig/osaka-now/internal/feed"
	"github.com/ian-ledig/osaka-now/internal/store"
)

// Service is what the window can call. It owns the fetched lists and keeps
// them fresh in the background.
type Service struct {
	app      *application.App
	onStatus func(Status) // lets main update the tray tooltip

	mu    sync.Mutex
	cache store.Cache
	busy  bool
}

// Status is shown under the title and in the tray tooltip.
type Status struct {
	Updated string `json:"updated"` // "Mon 14:31", or "" before the first fetch
	Busy    bool   `json:"busy"`
}

func newService() *Service { return &Service{cache: store.Load()} }

// Events returns the events worth showing today, best first.
func (s *Service) Events() []feed.Entry {
	s.mu.Lock()
	defer s.mu.Unlock()
	return feed.RankEvents(s.cache.Events, feed.Today())
}

// Movies returns the films now showing at TOHO Cinemas, newest first.
func (s *Service) Movies() []feed.Entry {
	s.mu.Lock()
	defer s.mu.Unlock()
	return feed.RankMovies(s.cache.Movies, feed.Today())
}

func (s *Service) Theaters() []feed.Theater { return feed.Theaters }

// Showtimes returns a film's showings at a theater. Pass no days to also
// learn which days have showings, and no day to get the first of them.
func (s *Service) Showtimes(code, theater, day string, days []string) (feed.Showtimes, error) {
	return feed.FetchShowtimes(code, theater, day, days)
}

func (s *Service) Status() Status {
	s.mu.Lock()
	defer s.mu.Unlock()
	st := Status{Busy: s.busy}
	if !s.cache.Fetched.IsZero() {
		st.Updated = s.cache.Fetched.Format("Mon 15:04")
	}
	return st
}

// Open shows a page in the default browser. The address comes from a web
// page, so anything that is not http(s) is ignored.
func (s *Service) Open(address string) {
	if feed.IsWeb(address) {
		_ = s.app.Browser.OpenURL(address)
	}
}

// run checks every 30 minutes whether a refresh is due. One short timer
// rather than a six-hour one, so sleep and failed fetches catch up.
func (s *Service) run() {
	time.Sleep(15 * time.Second) // not during startup
	for {
		s.refresh(false)
		time.Sleep(30 * time.Minute)
	}
}

func (s *Service) refresh(force bool) {
	s.mu.Lock()
	eventsDue := force || time.Since(s.cache.Fetched) >= feed.RefreshHours*time.Hour
	// No movies means a first run, or that TOHO failed last time.
	if s.busy || !(eventsDue || len(s.cache.Movies) == 0) {
		s.mu.Unlock()
		return
	}
	s.busy = true
	released := maps.Clone(s.cache.Released)
	s.mu.Unlock()
	s.notify()

	quiet := func(string) {}
	var events []feed.Entry
	if eventsDue {
		events = feed.FetchEvents(quiet)
	}
	movies, err := feed.FetchMovies(released, feed.Today(), quiet)

	s.mu.Lock()
	if len(events) > 0 {
		s.cache.Events, s.cache.Fetched = events, time.Now()
	}
	if err == nil && len(movies) > 0 {
		s.cache.Movies, s.cache.Released = movies, released
	}
	s.busy = false
	cache := s.cache
	s.mu.Unlock()

	if !cache.Fetched.IsZero() {
		_ = store.Save(cache) // the cache is only a convenience
	}
	s.notify()
}

func (s *Service) notify() {
	if s.onStatus != nil {
		s.onStatus(s.Status())
	}
	s.app.Event.Emit("updated")
}
