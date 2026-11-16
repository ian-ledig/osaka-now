// Package store keeps the last fetched lists on disk, so the window has
// something to show at once and when offline.
package store

import (
	"encoding/json"
	"os"
	"path/filepath"
	"time"

	"github.com/ian-ledig/osaka-now/internal/feed"
)

type Cache struct {
	Fetched  time.Time         `json:"fetched"`
	Events   []feed.Entry      `json:"events"`
	Movies   []feed.Entry      `json:"movies"`
	Released map[string]string `json:"released"` // film code to 2006-01-02, or "" when no date was found
}

// Dir is where the cache and the thumbnails live: %LocalAppData%\OsakaNow,
// ~/Library/Caches/OsakaNow or ~/.cache/OsakaNow.
func Dir() string {
	base, err := os.UserCacheDir()
	if err != nil {
		base = os.TempDir()
	}
	return filepath.Join(base, "OsakaNow")
}

func file() string { return filepath.Join(Dir(), "cache.json") }

// Load returns the saved lists, or an empty cache if there are none yet or
// the file can't be read. Either way the app just fetches again.
func Load() Cache {
	var c Cache
	if data, err := os.ReadFile(file()); err == nil {
		if json.Unmarshal(data, &c) != nil {
			c = Cache{}
		}
	}
	if c.Released == nil {
		c.Released = map[string]string{}
	}
	return c
}

func Save(c Cache) error {
	data, err := json.Marshal(c)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(Dir(), 0o755); err != nil {
		return err
	}
	return os.WriteFile(file(), data, 0o644)
}
