package feed

import (
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"

	"golang.org/x/text/encoding/japanese"
)

const (
	userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36"
	maxBody   = 8 << 20 // nothing we read is anywhere near 8 MB
)

var client = &http.Client{Timeout: 20 * time.Second}

// pause is the wait between two requests to the same site.
var pause = time.Second

// GetBytes downloads a page or a picture.
func GetBytes(address string) ([]byte, string, error) {
	req, err := http.NewRequest(http.MethodGet, address, nil)
	if err != nil {
		return nil, "", err
	}
	req.Header.Set("User-Agent", userAgent)
	resp, err := client.Do(req)
	if err != nil {
		return nil, "", err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, "", fmt.Errorf("%s: %s", address, resp.Status)
	}
	data, err := io.ReadAll(io.LimitReader(resp.Body, maxBody))
	return data, resp.Header.Get("Content-Type"), err
}

func getText(address string) (string, error) {
	data, contentType, err := GetBytes(address)
	if err != nil {
		return "", err
	}
	return decode(data, contentType), nil
}

// decode handles the one non-UTF-8 encoding the sources use: Jalan serves
// Shift_JIS and calls it Windows-31J.
func decode(data []byte, contentType string) string {
	cs := strings.ToLower(contentType)
	if strings.Contains(cs, "31j") || strings.Contains(cs, "shift_jis") || strings.Contains(cs, "sjis") {
		if out, err := japanese.ShiftJIS.NewDecoder().Bytes(data); err == nil {
			return string(out)
		}
	}
	return string(data)
}

// IsWeb reports whether a URL taken from a web page is safe to open in a browser.
func IsWeb(address string) bool {
	u, err := url.Parse(address)
	return err == nil && (u.Scheme == "https" || u.Scheme == "http") && u.Host != ""
}

var imageHosts = []string{"walkerplus.com", "jalan.net", "tohotheater.jp"}

// IsImage reports whether a picture URL points at one of the source sites.
// Pictures are not fetched from anywhere else.
func IsImage(address string) bool {
	if !IsWeb(address) {
		return false
	}
	u, _ := url.Parse(address)
	host := u.Hostname()
	for _, d := range imageHosts {
		if host == d || strings.HasSuffix(host, "."+d) {
			return true
		}
	}
	return false
}
