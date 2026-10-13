import { api, describe } from "../api.js";
import { clear, h } from "../dom.js";
import { confirmButton, field, pill, statusLine } from "../ui.js";

export async function setupView(root) {
  const status = statusLine();
  const categoryList = h("ul", { "aria-label": "Categories" });
  const accountList = h("ul", { "aria-label": "Accounts" });

  async function refresh() {
    const [categories, accounts] = await Promise.all([
      api("GET", "/categories"),
      api("GET", "/accounts?includeArchived=true"),
    ]);
    clear(categoryList, categories.length === 0 ? h("li", { class: "muted" }, "No categories yet.") : categories.map(categoryRow));
    clear(accountList, accounts.length === 0 ? h("li", { class: "muted" }, "No accounts yet.") : accounts.map(accountRow));
  }

  async function run(action, success) {
    status.clear();
    try {
      await action();
      await refresh();
      if (success) status.ok(success);
    } catch (e) {
      status.error(describe(e));
    }
  }

  const categoryRow = (c) => h("li", {}, c.name, " ",
    confirmButton("Delete", "Confirm delete",
      () => run(() => api("DELETE", `/categories/${c.id}`), `Deleted ${c.name}.`),
      { ariaLabel: `Delete category ${c.name}` }));

  const accountRow = (a) => h("li", {}, a.name, " ", h("span", { class: "pill" }, a.type),
    a.invertSign
      ? pill("purchases shown positive", "This account's CSV exports list purchases as positive numbers, as many credit cards do. Imports flip the sign so money out is always stored as negative.")
      : null,
    a.archivedAt
      ? pill("archived", "Hidden from lists and closed to new transactions. Its history is kept, and you can reopen it.")
      : null, " ",
    a.archivedAt
      ? h("button", { type: "button", class: "secondary", "aria-label": `Reopen account ${a.name}`,
          onclick: () => run(() => api("POST", `/accounts/${a.id}/unarchive`), `Reopened ${a.name}.`) }, "Reopen")
      : confirmButton("Archive", "Confirm archive",
          () => run(() => api("DELETE", `/accounts/${a.id}`), `Archived ${a.name}.`),
          { ariaLabel: `Archive account ${a.name}` }));

  const categoryName = h("input", { name: "category-name", required: true, maxlength: 100 });
  const accountName = h("input", { name: "account-name", required: true, maxlength: 100 });
  const accountType = h("select", { name: "account-type" },
    ["Checking", "Savings", "Credit"].map((t) => h("option", { value: t }, t)));
  const invert = h("input", { type: "checkbox", name: "invert" });

  clear(root,
    h("h1", {}, "Accounts and categories"),
    status.el,
    h("div", { class: "panel" },
      h("h2", {}, "Categories"),
      categoryList,
      h("form", { class: "row", onsubmit: (event) => {
        event.preventDefault();
        run(async () => { await api("POST", "/categories", { name: categoryName.value }); categoryName.value = ""; }, "Category added.");
      } }, field("New category", categoryName), h("button", { type: "submit" }, "Add category"))),
    h("div", { class: "panel" },
      h("h2", {}, "Accounts"),
      accountList,
      h("form", { class: "row", onsubmit: (event) => {
        event.preventDefault();
        run(async () => {
          await api("POST", "/accounts", { name: accountName.value, type: accountType.value, invertSign: invert.checked });
          accountName.value = "";
        }, "Account added.");
      } },
      field("New account", accountName), field("Type", accountType),
      h("label", {}, "Its CSV exports show purchases as positive numbers",
        invert, h("span", { class: "muted" }, "Tick for most credit cards: imports will flip the sign so money out is stored as negative.")),
      h("button", { type: "submit" }, "Add account"))));

  await refresh();
}
