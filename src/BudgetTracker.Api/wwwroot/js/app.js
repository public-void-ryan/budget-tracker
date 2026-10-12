import { api, describe, session, sessionExpired } from "./api.js";
import { h, clear } from "./dom.js";
import { authView } from "./views/auth.js";
import { homeView } from "./views/home.js";
import { setupView } from "./views/setup.js";
import { transactionsView } from "./views/transactions.js";

const root = document.getElementById("app");
const bar = document.getElementById("bar");
const nav = document.getElementById("nav");
const who = document.getElementById("who");

// Add a screen here: [hash, label, view]. Views render into `root` and get `{ user }`.
const routes = [
  ["#/", "Overview", homeView],
  ["#/transactions", "Transactions", transactionsView],
  ["#/setup", "Accounts and categories", setupView],
];

let user = null;

async function start() {
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
      location.hash = "#/";
      showApp();
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
  const hash = location.hash || "#/";
  const [, , view] = routes.find(([path]) => path === hash) ?? routes[0];
  clear(nav, routes.map(([path, label]) => h("a", { href: path, "aria-current": path === hash ? "page" : false }, label)));
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
  location.hash = "";
  showAuth();
});
sessionExpired.addEventListener("expired", showAuth);
window.addEventListener("hashchange", renderRoute);

start();
