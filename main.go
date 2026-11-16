// Osaka Now: events and TOHO Cinemas showtimes around Osaka, in the system tray.
package main

import (
	"embed"
	"fmt"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"slices"

	"github.com/wailsapp/wails/v3/pkg/application"
	"github.com/wailsapp/wails/v3/pkg/events"

	"github.com/ian-ledig/osaka-now/internal/feed"
	"github.com/ian-ledig/osaka-now/internal/store"
	"github.com/ian-ledig/osaka-now/internal/thumb"
)

//go:embed all:frontend/dist
var assets embed.FS

//go:embed build/appicon.png
var icon []byte

const version = "2.0.0" // keep in step with build/config.yml

func main() {
	if slices.Contains(os.Args, "--version") {
		fmt.Println(version)
		return
	}
	if slices.Contains(os.Args, "--dump") {
		dump()
		return
	}
	// --show opens the window at once and keeps it open: for screenshots and
	// for working on the layout.
	pinned := slices.Contains(os.Args, "--show")

	svc := newService()
	var tray *application.SystemTray
	pages := http.NewServeMux()
	pages.Handle("/thumb", thumb.Handler(filepath.Join(store.Dir(), "thumbs")))
	pages.Handle("/", application.AssetFileServerFS(assets))

	app := application.New(application.Options{
		Name:        "Osaka Now",
		Description: "Events and TOHO Cinemas showtimes around Osaka",
		Services:    []application.Service{application.NewService(svc)},
		Assets:      application.AssetOptions{Handler: pages},
		Mac:         application.MacOptions{ActivationPolicy: application.ActivationPolicyAccessory}, // no Dock icon
		SingleInstance: &application.SingleInstanceOptions{
			UniqueID: "io.github.ian-ledig.osaka-now",
			// Starting the app again opens the window of the one already running.
			OnSecondInstanceLaunch: func(application.SecondInstanceData) { tray.ShowWindow() },
		},
	})
	svc.app = app

	window := app.Window.NewWithOptions(application.WebviewWindowOptions{
		Name:             "main",
		Title:            "Osaka Now",
		Width:            496,
		Height:           760,
		Frameless:        true,
		AlwaysOnTop:      true,
		DisableResize:    true,
		Hidden:           !pinned,
		HideOnFocusLost:  !pinned,
		BackgroundColour: application.NewRGB(32, 32, 32),
		Windows:          application.WindowsWindow{HiddenOnTaskbar: true},
		URL:              "/",
	})
	// The window is as tall as the screen allows; the tray decides where it goes.
	app.Event.OnApplicationEvent(events.Common.ApplicationStarted, func(*application.ApplicationEvent) {
		if screen := app.Screen.GetPrimary(); screen != nil && !pinned {
			window.SetSize(496, screen.WorkArea.Height-16)
		}
	})
	window.OnWindowEvent(events.Common.WindowShow, func(*application.WindowEvent) {
		app.Event.Emit("shown") // the page reloads its lists when it is opened, never while it is being read
	})

	tray = app.SystemTray.New()
	tray.SetIcon(icon)
	tray.SetTooltip("Osaka Now")
	svc.onStatus = func(st Status) {
		switch {
		case st.Busy:
			tray.SetTooltip("Osaka Now: updating…")
		case st.Updated != "":
			tray.SetTooltip("Osaka Now (updated " + st.Updated + ")")
		}
	}

	menu := app.NewMenu()
	// "Open" is in the menu too, because some Linux desktops only show the menu on a click.
	menu.Add("Open").OnClick(func(*application.Context) { tray.ShowWindow() })
	menu.Add("Refresh now").OnClick(func(*application.Context) { go svc.refresh(true) })
	status, _ := app.Autostart.Status()
	login := menu.AddCheckbox("Start at login", status.Enabled)
	login.OnClick(func(*application.Context) {
		if now, _ := app.Autostart.Status(); now.Enabled {
			_ = app.Autostart.Disable()
		} else {
			_ = app.Autostart.Enable()
		}
		now, _ := app.Autostart.Status()
		login.SetChecked(now.Enabled) // shows what actually happened
	})
	menu.AddSeparator()
	menu.Add("Quit").OnClick(func(*application.Context) { app.Quit() })
	tray.SetMenu(menu)
	tray.AttachWindow(window).WindowOffset(8)

	go svc.run()
	if err := app.Run(); err != nil {
		log.Fatal(err)
	}
}

// dump fetches everything once and prints both lists with their scores, to
// see what the sources returned.
func dump() {
	today := feed.Today()
	say := func(s string) { fmt.Println(s) }
	for _, e := range feed.RankEvents(feed.FetchEvents(say), today) {
		fmt.Printf("%.0f\t%s\t%s\t%s\t%s\t%s\n", e.Score, e.When, e.Tag, e.Name, e.Place, e.URL)
	}
	movies, err := feed.FetchMovies(map[string]string{}, today, say)
	if err != nil {
		fmt.Println("FAIL\t" + err.Error())
	}
	for _, e := range feed.RankMovies(movies, today) {
		fmt.Printf("%s\t%s\t%s\t%s\n", e.When, e.Name, e.Tag, e.URL)
	}
}
