import { api, describe } from "../api.js";
import { clear, formatMoney, h } from "../dom.js";
import { confirmButton, field, getMonth, monthPicker, parseAmount, previousMonth, statusLine } from "../ui.js";

export async function budgetsView(root) {
  const status = statusLine();
  const body = h("tbody");
  const categories = await api("GET", "/categories");
  let month = getMonth();

  const picker = monthPicker((m) => { month = m; status.clear(); load(); });

  // Only the latest request may draw: a slow answer for a month you have already left must
  // not replace the rows (and whatever you typed into them) for the month you are on.
  let latest = 0;

  async function load() {
    const mine = ++latest;
    body.setAttribute("aria-busy", "true");
    try {
      const budgets = await api("GET", `/budgets?month=${month}`);
      if (mine !== latest) return;
      const amounts = new Map(budgets.map((b) => [b.categoryId, b.amount]));
      clear(body, categories.map((c) => row(c, amounts.get(c.id))));
    } catch (e) {
      if (mine === latest) status.error(describe(e));
    } finally {
      if (mine === latest) body.setAttribute("aria-busy", "false");
    }
  }

  function row(category, amount) {
    const input = h("input", { name: `budget-${category.name}`, inputmode: "decimal", value: amount === undefined ? "" : String(amount),
      "aria-label": `Budget for ${category.name}`, placeholder: "none" });
    return h("tr", {},
      h("td", {}, category.name),
      h("td", {}, h("form", { class: "row", novalidate: true, onsubmit: async (event) => {
        event.preventDefault();
        status.clear();
        const value = parseAmount(input.value);
        if (value === null || value < 0) return status.error("Enter an amount of zero or more, with at most 4 decimals.");
        try {
          await api("PUT", `/budgets/${month}/${category.id}`, { amount: value });
          status.ok(`Saved ${category.name}: ${formatMoney(value)}.`);
          await load();
        } catch (e) {
          status.error(describe(e));
        }
      } }, input, h("button", { type: "submit", "aria-label": `Save ${category.name} budget` }, "Save"),
      amount === undefined ? null : confirmButton("Remove", "Confirm remove", async () => {
        status.clear();
        try {
          await api("DELETE", `/budgets/${month}/${category.id}`);
          status.ok(`Removed the ${category.name} budget.`);
          await load();
        } catch (e) {
          status.error(describe(e));
        }
      }, { ariaLabel: `Remove ${category.name} budget` }))));
  }

  const copy = h("button", { type: "button", class: "secondary", onclick: async () => {
    status.clear();
    try {
      const r = await api("POST", `/budgets/copy?from=${previousMonth(month)}&to=${month}`);
      status.ok(`Copied ${r.copied} budget${r.copied === 1 ? "" : "s"} from ${previousMonth(month)}; ${r.skippedExisting} already set here were kept.`);
      await load();
    } catch (e) {
      status.error(describe(e));
    }
  } }, "Copy last month’s budgets");

  clear(root,
    h("h1", {}, "Budgets"),
    categories.length === 0
      ? h("p", {}, "Create categories under Accounts and categories first.")
      : [
        h("div", { class: "toolbar" }, field("Month", picker), copy),
        status.el,
        h("table", {}, h("thead", {}, h("tr", {}, h("th", {}, "Category"), h("th", {}, "Planned spending"))), body),
      ]);

  if (categories.length > 0) await load();
}
