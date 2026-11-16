# Contributing

Bug reports, ideas and pull requests are welcome.

## Reporting a problem

[Open an issue](https://github.com/ian-ledig/osaka-now/issues/new/choose) and say which system you are on. If a list is empty or wrong, attach the output of `OsakaNow --dump > ranking.txt`. It shows what each source returned.

## Setting up

You need [Go](https://go.dev/dl/) 1.26 or newer, [Node.js](https://nodejs.org/) and the Wails 3 command-line tool:

```sh
go install github.com/wailsapp/wails/v3/cmd/wails3@v3.1.0
```

On Linux you also need the GTK 4 and WebKitGTK 6 headers. On Ubuntu or Debian:

```sh
sudo apt install libgtk-4-dev libwebkitgtk-6.0-dev
```

Then, from the repository:

```sh
wails3 dev               # run with live reload
wails3 build             # build bin/OsakaNow
go test ./internal/...   # run the tests
```

`OsakaNow --show` opens the window straight away and keeps it open, which is handy when working on the layout.

## Where things are

| Path | What is in it |
| --- | --- |
| `main.go`, `service.go` | The tray icon, the window, and the methods the page can call |
| `internal/feed` | Fetching, parsing and ranking events, films and showtimes. `tuning.go` holds the sources and keywords |
| `internal/thumb` | Downloads, trims and shrinks the pictures |
| `internal/store` | The cache on disk |
| `frontend/` | The page shown in the window: plain TypeScript and CSS, no framework |
| `build/` | Wails build files for each system (icons, manifests, packaging) |

## A few rules

- Don't make it fetch more often, and keep the pauses between requests.
- Anything read from a web page is untrusted. If it ends up in a URL, a file name or the page, validate it first. In the frontend, text from the web only goes in through `textContent`.
- If you add a parser or change the scoring, add a test next to it.
- Keep the frontend free of frameworks and runtime dependencies.
- Run `gofmt` before committing.

## Commits

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `docs:`, `refactor:`, `ci:`, `chore:`.

Keep a pull request to one topic. If the window looks different afterwards, add a screenshot and say which system it was taken on.

## Adding a city or a source

Event sources are the `Sources` list in `internal/feed/tuning.go`. Each entry is a listing URL, a popularity bonus and a flag. Any Walkerplus or Jalan area listing works without other changes. For a different site, write a parser that returns `Entry` values and merge its results in `FetchEvents`.

## Releasing

Set the version in `main.go` and `build/config.yml`, run `wails3 task common:update:build-assets`, commit, then push a `v*` tag. The build workflow creates the release with the files for all three systems.
