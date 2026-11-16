// Package thumb serves the small pictures shown next to each row. A picture
// is downloaded once, trimmed, shrunk and kept on disk.
package thumb

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"image"
	_ "image/gif"
	"image/jpeg"
	_ "image/png"
	"net/http"
	"os"
	"path/filepath"
	"sync"
	"time"

	"golang.org/x/image/draw"
	_ "golang.org/x/image/webp"

	"github.com/ian-ledig/osaka-now/internal/feed"
)

const (
	width, height = 224, 160            // twice the 112x80 a row shows, so it stays sharp on high-DPI screens
	keep          = 30 * 24 * time.Hour // unused pictures are deleted after this
)

// Handler answers GET /thumb?u=<picture URL>. Only pictures from the source
// sites are fetched; anything else is a 404.
func Handler(dir string) http.Handler {
	var fetching sync.Mutex // one download at a time, to go easy on the sites
	go prune(dir)
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		address := r.URL.Query().Get("u")
		if !feed.IsImage(address) {
			http.NotFound(w, r)
			return
		}
		sum := sha256.Sum256([]byte(address))
		file := filepath.Join(dir, hex.EncodeToString(sum[:16])+".jpg")
		if _, err := os.Stat(file); err != nil {
			fetching.Lock()
			if _, err := os.Stat(file); err != nil { // not made while we waited
				err = create(address, file)
				time.Sleep(150 * time.Millisecond)
				if err != nil {
					fetching.Unlock()
					http.NotFound(w, r)
					return
				}
			}
			fetching.Unlock()
		}
		w.Header().Set("Cache-Control", "max-age=86400")
		http.ServeFile(w, r, file)
	})
}

func create(address, file string) error {
	data, _, err := feed.GetBytes(address)
	if err != nil {
		return err
	}
	src, _, err := image.Decode(bytes.NewReader(data))
	if err != nil {
		return err
	}
	dst := image.NewRGBA(image.Rect(0, 0, width, height))
	draw.CatmullRom.Scale(dst, dst.Bounds(), src, cover(trimWhite(src), width, height), draw.Src, nil)
	var out bytes.Buffer
	if err := jpeg.Encode(&out, dst, &jpeg.Options{Quality: 88}); err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(file), 0o755); err != nil {
		return err
	}
	return os.WriteFile(file, out.Bytes(), 0o644)
}

// trimWhite finds the photo inside the white bars the sites letterbox it
// with. Sixteen samples per line are enough to tell.
func trimWhite(img image.Image) image.Rectangle {
	b := img.Bounds()
	white := func(x, y int) bool {
		r, g, bl, _ := img.At(x, y).RGBA()
		return r>>8 > 240 && g>>8 > 240 && bl>>8 > 240
	}
	row := func(y int) bool {
		for i := 0; i < 16; i++ {
			if !white(b.Min.X+i*(b.Dx()-1)/15, y) {
				return false
			}
		}
		return true
	}
	col := func(x int) bool {
		for i := 0; i < 16; i++ {
			if !white(x, b.Min.Y+i*(b.Dy()-1)/15) {
				return false
			}
		}
		return true
	}
	for b.Dy() > 16 && row(b.Min.Y) {
		b.Min.Y++
	}
	for b.Dy() > 16 && row(b.Max.Y-1) {
		b.Max.Y--
	}
	for b.Dx() > 16 && col(b.Min.X) {
		b.Min.X++
	}
	for b.Dx() > 16 && col(b.Max.X-1) {
		b.Max.X--
	}
	return b
}

// cover returns the largest centred part of r with the ratio w:h.
func cover(r image.Rectangle, w, h int) image.Rectangle {
	cw, ch := r.Dx(), r.Dx()*h/w
	if ch > r.Dy() {
		cw, ch = r.Dy()*w/h, r.Dy()
	}
	x, y := r.Min.X+(r.Dx()-cw)/2, r.Min.Y+(r.Dy()-ch)/2
	return image.Rect(x, y, x+cw, y+ch)
}

func prune(dir string) {
	files, _ := os.ReadDir(dir)
	for _, f := range files {
		if info, err := f.Info(); err == nil && time.Since(info.ModTime()) > keep {
			os.Remove(filepath.Join(dir, f.Name()))
		}
	}
}
