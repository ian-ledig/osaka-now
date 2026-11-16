package thumb

import (
	"image"
	"image/color"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestTrimWhiteAndCover(t *testing.T) {
	// A 100x60 photo letterboxed inside a white 100x100 image.
	img := image.NewRGBA(image.Rect(0, 0, 100, 100))
	for y := 0; y < 100; y++ {
		for x := 0; x < 100; x++ {
			c := color.RGBA{255, 255, 255, 255}
			if y >= 20 && y < 80 {
				c = color.RGBA{uint8(x), 90, 120, 255}
			}
			img.Set(x, y, c)
		}
	}
	photo := trimWhite(img)
	if photo != image.Rect(0, 20, 100, 80) {
		t.Fatalf("white bars not trimmed: %v", photo)
	}
	if got := cover(photo, 224, 160); got != image.Rect(8, 20, 92, 80) {
		t.Fatalf("crop to 7:5: %v", got)
	}
}

func TestHandlerRefusesOtherHosts(t *testing.T) {
	h := Handler(t.TempDir())
	for _, u := range []string{"", "file:///etc/passwd", "http://127.0.0.1/a.jpg", "https://example.com/a.jpg"} {
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/thumb?u="+u, nil))
		if rec.Code != http.StatusNotFound {
			t.Errorf("%q: got %d, want 404", u, rec.Code)
		}
	}
}
