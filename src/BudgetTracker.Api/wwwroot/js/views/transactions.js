import { api, describe } from "../api.js";
import { clear, formatMoney, h } from "../dom.js";
import { confirmButton, field, parseAmount, pill, statusLine, today, TRANSFER_TIP } from "../ui.js";

const PAGE = 25;
const UNCATEGORIZED = "__uncategorized__";   // a filter choice, never a real category id

export async function transactionsView(root) {
  const status = statusLine();
  const [accounts, categories] = await Promise.all([
    api("GET", "/accounts?includeArchived=true"),
    api("GET", "/categories"),
  ]);
  const accountName = new Map(accounts.map((a) => [a.id, a.name]));
  const openAccounts = accounts.filter((a) => !a.archivedAt);

  const categoryOptions = (selected) => [
    h("option", { value: "" }, "Uncategorized"),
    categories.map((c) => h("option", { value: c.id, selected: c.id === selected }, c.name)),
  ];

  // ---- list ----
  const body = h("tbody");
  const more = h("button", { type: "button", class: "secondary", hidden: true }, "Load more");
  const empty = h("p", { class: "muted", hidden: true }, "No transactions match.");
  let cursor = null;

  const filters = {
    account: h("select", { name: "filter-account" }, h("option", { value: "" }, "All accounts"),
      accounts.map((a) => h("option", { value: a.id }, a.name))),
    category: h("select", { name: "filter-category" }, h("option", { value: "" }, "All categories"),
      h("option", { value: UNCATEGORIZED }, "Uncategorized"),
      categories.map((c) => h("option", { value: c.id }, c.name))),
    from: h("input", { type: "date", name: "filter-from" }),
    to: h("input", { type: "date", name: "filter-to" }),
  };

  function query() {
    const q = new URLSearchParams({ limit: String(PAGE) });
    if (filters.account.value) q.set("accountId", filters.account.value);
    if (filters.category.value === UNCATEGORIZED) q.set("uncategorized", "true");
    else if (filters.category.value) q.set("categoryId", filters.category.value);
    if (filters.from.value) q.set("from", filters.from.value);
    if (filters.to.value) q.set("to", filters.to.value);
    if (cursor) q.set("cursor", cursor);
    return q;
  }

  // A newer search supersedes an older one still in flight; its rows must not be appended.
  let latest = 0;

  async function load(reset) {
    const mine = ++latest;
    status.clear();
    if (reset) { cursor = null; clear(body); }
    try {
      const page = await api("GET", `/transactions?${query()}`);
      if (mine !== latest) return;
      body.append(...page.items.map(row));
      cursor = page.nextCursor;
      more.hidden = cursor === null;
      empty.hidden = body.children.length > 0;
    } catch (e) {
      status.error(describe(e));
    }
  }

  function row(t) {
    const description = h("td", {}, t.description, t.isTransfer ? [" ", pill("transfer", TRANSFER_TIP)] : null);
    const select = h("select", {
      "aria-label": `Category for ${t.description}`,
      onchange: async () => {
        status.clear();
        try {
          await api("PUT", `/transactions/${t.id}`, {
            accountId: t.accountId, categoryId: select.value || null, postedOn: t.postedOn,
            amount: t.amount, description: t.description, isTransfer: t.isTransfer,
          });
          t.categoryId = select.value || null;
          status.ok("Category saved.");
        } catch (e) {
          select.value = t.categoryId ?? "";
          status.error(describe(e));
        }
      },
    }, categoryOptions(t.categoryId));
    const tr = h("tr", {},
      h("td", {}, t.postedOn), description, h("td", {}, accountName.get(t.accountId) ?? ""),
      h("td", {}, select),
      h("td", { class: "num" }, formatMoney(t.amount)),
      h("td", {}, confirmButton("Delete", "Confirm delete", async () => {
        status.clear();
        try {
          await api("DELETE", `/transactions/${t.id}`);
          tr.remove();
          empty.hidden = body.children.length > 0;
          status.ok("Transaction deleted.");
        } catch (e) {
          status.error(describe(e));
        }
      }, { ariaLabel: `Delete ${t.description}` })));
    return tr;
  }

  // ---- add form ----
  const add = {
    account: h("select", { name: "account", required: true }, openAccounts.map((a) => h("option", { value: a.id }, a.name))),
    date: h("input", { type: "date", name: "date", required: true, value: today() }),
    amount: h("input", { name: "amount", required: true, inputmode: "decimal", placeholder: "-12.50" }),
    description: h("input", { name: "description", required: true, maxlength: 500 }),
    category: h("select", { name: "category" }, categoryOptions(null)),
    transfer: h("input", { type: "checkbox", name: "transfer" }),
  };
  const addForm = h("form", { class: "row", novalidate: true, onsubmit: async (event) => {
    event.preventDefault();
    status.clear();
    const amount = parseAmount(add.amount.value);
    if (amount === null) return status.error("Enter the amount as a number with at most 4 decimals; negative is money out.");
    try {
      await api("POST", "/transactions", {
        accountId: add.account.value, categoryId: add.category.value || null, postedOn: add.date.value,
        amount, description: add.description.value, isTransfer: add.transfer.checked,
      });
      add.amount.value = ""; add.description.value = "";
      await load(true);
      status.ok("Transaction added.");
    } catch (e) {
      status.error(describe(e));
    }
  } },
  field("Account", add.account), field("Date", add.date), field("Amount", add.amount),
  field("Description", add.description), field("Category", add.category),
  h("label", {}, "Transfer", add.transfer),
  h("button", { type: "submit" }, "Add transaction"));

  more.addEventListener("click", () => load(false));
  clear(root,
    h("h1", {}, "Transactions"),
    status.el,
    openAccounts.length === 0
      ? h("p", {}, "Create an account under Accounts and categories first.")
      : h("details", { class: "panel" }, h("summary", {}, "Add a transaction"), addForm),
    h("form", { class: "toolbar", "aria-label": "Filters", onsubmit: (event) => { event.preventDefault(); load(true); } },
      field("Account", filters.account), field("Category", filters.category),
      field("From", filters.from), field("To", filters.to),
      h("button", { type: "submit", class: "secondary" }, "Apply filters")),
    h("table", {},
      h("thead", {}, h("tr", {}, ["Date", "Description", "Account", "Category"].map((c) => h("th", {}, c)),
        h("th", { class: "num" }, "Amount"), h("th", {}, h("span", { class: "sr-only" }, "Actions")))),
      body),
    empty, more);

  await load(true);
}
