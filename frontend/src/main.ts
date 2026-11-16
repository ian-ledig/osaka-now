import { Events, Window } from "@wailsio/runtime";
import { Service } from "../bindings/github.com/ian-ledig/osaka-now";
import type { Entry } from "../bindings/github.com/ian-ledig/osaka-now/internal/feed";

type View = "events" | "movies" | "times";

const get = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const lists: Record<View, HTMLElement> = { events: get("events"), movies: get("movies"), times: get("times") };
const tabs = { events: get("tab-events"), movies: get("tab-movies") };
const bar = get("bar");

let tab: "events" | "movies" = "events";
let film: Entry | null = null;       // the film whose showtimes are open
let theater = "";
let days: string[] | null = null;    // days with showings for that film at that theater
let request = 0;                     // answers to a request the user has moved on from are dropped
let empty = true;                    // nothing fetched yet, so the lists may be filled in as soon as data arrives
const scroll: Record<View, number> = { events: 0, movies: 0, times: 0 };

// Text from the web only ever goes in through textContent, never as HTML.
function el(tag: string, className = "", text = ""): HTMLElement {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text) node.textContent = text;
  return node;
}

function row(e: Entry, picture: boolean, pick: (() => void) | null): HTMLElement {
  const button = el("button", "row") as HTMLButtonElement;
  button.type = "button";
  if (picture) {
    const pic = el("span", "pic");
    if (e.img) {
      const img = document.createElement("img");
      img.alt = "";
      img.loading = "lazy";
      img.src = "/thumb?u=" + encodeURIComponent(e.img);
      img.onerror = () => img.remove(); // keep the blank box
      pic.append(img);
    }
    button.append(pic);
  }
  const text = el("span", "text");
  text.append(el("span", "name", e.name));
  if (e.when) {
    const line = el("span", "line");
    line.append(el("span", "pill " + e.badge, e.when), el("span", "tag", e.tag));
    text.append(line);
  }
  if (e.place) text.append(el("span", "place", e.place));
  button.append(text);
  if (pick) button.addEventListener("click", pick);
  else button.disabled = true;
  const item = el("li");
  item.append(button);
  return item;
}

function fill(list: HTMLElement, rows: HTMLElement[], none: string) {
  list.replaceChildren(...(rows.length ? rows : [el("li", "message", none)]));
}

function show(view: View) {
  const current = (Object.keys(lists) as View[]).find((v) => !lists[v].hidden);
  if (current) scroll[current] = lists[current].scrollTop;
  for (const v of Object.keys(lists) as View[]) lists[v].hidden = v !== view;
  lists[view].scrollTop = scroll[view];
  bar.hidden = view !== "times";
  tabs.events.setAttribute("aria-selected", String(tab === "events"));
  tabs.movies.setAttribute("aria-selected", String(tab === "movies"));
}

async function status() {
  const s = await Service.Status();
  const when = s.busy ? "updating…" : s.updated ? "updated " + s.updated : "no data yet";
  get("status").textContent = (tab === "events" ? "Coming up for two" : "Now at TOHO Cinemas, newest first") + "  ·  " + when;
  return s;
}

// Rebuilds both lists. Only called when the window opens or while it is still empty,
// so a list never reshuffles under someone who is reading it.
async function load() {
  const [events, movies, s] = await Promise.all([Service.Events(), Service.Movies(), status()]);
  const none = s.busy || !s.updated ? "Loading…" : "Nothing to show right now.";
  fill(lists.events, events.map((e) => row(e, true, () => Service.Open(e.url))), none);
  fill(lists.movies, movies.map((e) => row(e, true, () => openFilm(e))), none);
  empty = events.length === 0 && movies.length === 0;
}

function selectTab(next: "events" | "movies") {
  tab = next;
  film = null;
  request++;
  show(tab);
  status();
}

// Showtimes

function chip(text: string, on: boolean, pick: () => void): HTMLElement {
  const button = el("button", "chip", text);
  button.setAttribute("role", "radio");
  button.setAttribute("aria-checked", String(on));
  button.addEventListener("click", () => {
    for (const other of button.parentElement!.children) other.setAttribute("aria-checked", String(other === button));
    pick();
  });
  return button;
}

function dayLabel(day: string): string {
  const date = new Date(+day.slice(0, 4), +day.slice(4, 6) - 1, +day.slice(6, 8));
  if (date.toDateString() === new Date().toDateString()) return "Today";
  return date.toLocaleDateString("en-GB", { weekday: "short", day: "numeric" });
}

function openFilm(e: Entry) {
  film = e;
  get("film").textContent = e.name;
  scroll.times = 0;
  show("times");
  loadTimes("");
}

// day = "": ask again which days have showings at the current theater, and show the first.
async function loadTimes(day: string) {
  if (!film) return;
  const mine = ++request;
  fill(lists.times, [], "Loading showtimes…");
  try {
    const result = await Service.Showtimes(film.code, theater, day, day ? days : null);
    if (mine !== request) return; // the user picked something else meanwhile
    if (!day) {
      days = result.days;
      get("days").replaceChildren(...result.days.map((d) => chip(dayLabel(d), d === result.day, () => loadTimes(d))));
    }
    const none = result.day ? "No showings on this day." : "No showings here in the coming days.";
    fill(lists.times, result.rows.map((e) => row(e, false, e.url ? () => Service.Open(e.url) : null)), none);
  } catch {
    if (mine === request) fill(lists.times, [], "Could not load showtimes. Check the connection and pick the theater again.");
  }
}

function back() {
  film = null;
  request++;
  show(tab);
}

// Wiring

tabs.events.addEventListener("click", () => selectTab("events"));
tabs.movies.addEventListener("click", () => selectTab("movies"));
get("back").addEventListener("click", back);

document.addEventListener("keydown", (e) => {
  if (e.key === "Escape" || (e.key === "Backspace" && film)) {
    if (film) back();
    else Window.Hide();
  } else if (e.key === "ArrowDown" || e.key === "ArrowUp") {
    const rows = [...document.querySelectorAll<HTMLElement>("ul:not([hidden]) .row:not(:disabled)")];
    const next = rows[rows.indexOf(document.activeElement as HTMLElement) + (e.key === "ArrowDown" ? 1 : -1)] ?? rows[0];
    next?.focus();
    e.preventDefault();
  }
});
document.addEventListener("contextmenu", (e) => e.preventDefault());

Events.On("shown", () => {
  film = null;
  request++;
  show(tab);
  load();
});
Events.On("updated", () => (empty ? load() : status()));

Service.Theaters().then((all) => {
  theater = all[0].code;
  get("theaters").replaceChildren(...all.map((t, i) => chip(t.name, i === 0, () => { theater = t.code; loadTimes(""); })));
});
load();
