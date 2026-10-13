import { api, describe, session, sessionExpired } from "./api.js";
import { h, clear } from "./dom.js";
import { authView } from "./views/auth.js";
import { homeView } from "./views/home.js";
import { importView } from "./views/import.js";
import { rulesView } from "./views/rules.js";
import { budgetsView } from "./views/budgets.js";
import { setupView } from "./views/setup.js";
import { transactionsView } from "./views/transactions.js";

const root = document.getElementById("app");
const bar = document.getElementById("bar");
const nav = document.getElementById("nav");
const who = document.getElementById("who");

// Add a screen here: [path, label, view]. Views render into `root` and get `{ user }`.
// Paths live under /ui/ because the API owns the bare ones (GET /transactions is JSON); the
// server sends index.html for anything under /ui/, and this router takes it from there.
const routes = [
  ["/", "Overview", homeView],
  ["/ui/transactions", "Transactions", transactionsView],
  ["/ui/import", "Import", importView],
  ["/ui/budgets", "Budgets", budgetsView],
  ["/ui/rules", "Rules", rulesView],
  ["/ui/setup", "Accounts and categories", setupView],
];

let user = null;

/** The route path for the address bar: /ui and /ui/ are the overview, other trailing slashes are ignored. */
function currentPath() {
  const path = location.pathname.replace(/\/+$/, "") || "/";
  return path === "/ui" ? "/" : path;
}

function navigate(path) {
  if (path !== currentPath()) history.pushState(null, "", path);
  renderRoute();
}

// Links from before the router used the address path (#/transactions) still work.
function upgradeLegacyHash() {
  if (!location.hash.startsWith("#/")) return;
  const path = location.hash.slice(1);
  history.replaceState(null, "", path === "/" ? "/" : `/ui${path}`);
}

async function start() {
  upgradeLegacyHash();
  if (session.token) {
    try {
      user = await api("GET", "/me");
    } catch {
      session.clear();
    }
  }
  user ? showApp() : showAuth();
}

function showAuth() {
  user = null;
  bar.hidden = true;
  authView(root, {
    onSignedIn: async () => {
      user = await api("GET", "/me");
      showApp();   // stay on the address that was asked for, so a deep link survives logging in
    },
  });
}

function showApp() {
  bar.hidden = false;
  who.textContent = user.email;
  renderRoute();
}

function renderRoute() {
  if (!user) return;
  const path = currentPath();
  const route = routes.find(([p]) => p === path);
  const view = route ? route[2] : notFoundView;
  clear(nav, routes.map(([p, label]) => h("a", { href: p, "aria-current": p === path ? "page" : false }, label)));
  // Each render gets its own section, so a slow view that finishes after you navigated away
  // writes into a detached element instead of over the screen you are on now.
  const section = h("section");
  clear(root, section);
  Promise.resolve(view(section, { user })).catch((e) => {
    clear(section, h("p", { class: "error", role: "alert" }, describe(e)));
  });
  root.focus();
}

document.getElementById("logout").addEventListener("click", async () => {
  try {
    await api("POST", "/auth/logout");
  } catch {
    /* the session is dropped locally either way */
  }
  session.clear();
  history.pushState(null, "", "/");
  showAuth();
});
sessionExpired.addEventListener("expired", showAuth);
window.addEventListener("popstate", renderRoute);
// An old #/screen link opened in a tab that is already running the app.
window.addEventListener("hashchange", () => {
  upgradeLegacyHash();
  renderRoute();
});

// Same-page navigation for in-app links; modified clicks (new tab) and external links are left alone.
document.addEventListener("click", (event) => {
  if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
  const link = event.target.closest("a[href]");
  if (!link || link.target || link.origin !== location.origin) return;
  const target = link.pathname.replace(/\/+$/, "") || "/";
  if (!routes.some(([p]) => p === target)) return;
  event.preventDefault();
  if (user) navigate(link.pathname);
});

function notFoundView(root) {
  clear(root, h("h1", {}, "Page not found"), h("p", {}, h("a", { href: "/" }, "Back to the overview")));
}

start();
