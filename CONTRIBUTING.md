# Contributing

Bug reports, ideas and pull requests are welcome.

## Reporting a problem

[Open an issue](https://github.com/ian-ledig/osaka-now/issues/new/choose). If a list is empty or wrong, attach the output of `OsakaNow.exe --dump > ranking.txt`. It shows what each source returned.

## Building

You need Windows 10 or 11 and nothing else.

```bat
build.cmd
OsakaNow.exe --selftest
```

`--selftest` should print `selftest ok`. The build workflow runs the same two steps on every pull request.

## A few rules

- The whole app is [`OsakaNow.cs`](OsakaNow.cs). Please keep it that way: no packages, no project files.
- The compiler that ships with Windows only understands C# 5, so no string interpolation, `?.`, `out var` or expression-bodied members.
- Don't make it fetch more often, and keep the pauses between requests.
- Anything read from a web page is untrusted. If it ends up in a URL, a file name or a process start, validate it first. The existing code shows how.
- If you add a parser or change the scoring, add an assertion for it in `Core.SelfTest`.

## Commits

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `docs:`, `refactor:`, `ci:`, `chore:`.

Keep a pull request to one topic. If the window looks different afterwards, add a screenshot.

## Adding a city or a source

Event sources are the `Sources` array at the top of the `Core` class. Each entry is a listing URL, a popularity bonus and a flag. Any Walkerplus or Jalan area listing works without other changes. For a different site, write a parser that returns `Ev` rows and merge its results in `FetchAll`.
