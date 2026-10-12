import { api, describe } from "../api.js";
import { clear, formatMoney, h } from "../dom.js";
import { field, getMonth, monthPicker, statusLine } from "../ui.js";

export async function homeView(root) {
  const status = statusLine();
  const output = h("div", { "aria-live": "polite" });
  const picker = monthPicker(load);

  async function load(month = getMonth()) {
    status.clear();
    try {
      show(await api("GET", `/budgets/summary?month=${month}`));
    } catch (e) {
      clear(output);
      status.error(describe(e));
    }
  }

  function show(s) {
    if (s.categories.length === 0 && s.uncategorizedSpent === 0) {
      clear(output, h("p", { class: "muted" }, "Nothing to show for this month. Import transactions or set budgets to see your spending here."));
      return;
    }
    clear(output,
      h("table", { "aria-label": "Budget against actual" },
        h("thead", {}, h("tr", {}, h("th", {}, "Category"),
          ["Budget", "Spent", "Remaining"].map((c) => h("th", { class: "num" }, c)), h("th", {}, "Progress"))),
        h("tbody", {},
          s.categories.map(row),
          s.uncategorizedSpent !== 0
            ? h("tr", {}, h("td", {}, "Uncategorized"), h("td", { class: "num" }, "–"),
                h("td", { class: "num" }, formatMoney(s.uncategorizedSpent)), h("td", { class: "num" }, "–"), h("td"))
            : null),
        h("tfoot", {}, h("tr", {}, h("th", {}, "Total"),
          h("th", { class: "num" }, formatMoney(s.totals.budgeted)), h("th", { class: "num" }, formatMoney(s.totals.spent)),
          h("th", { class: "num" }, formatMoney(s.totals.budgeted - s.totals.spent)), h("th")))),
      h("p", { class: "muted" }, "Spent is money out minus refunds; transfers are left out. A category with income shows a negative amount."));
  }

  function row(c) {
    const hasBudget = c.budgeted !== null;
    return h("tr", {},
      h("td", {}, c.categoryName),
      h("td", { class: "num" }, hasBudget ? formatMoney(c.budgeted) : "–"),
      h("td", { class: "num" }, formatMoney(c.spent)),
      h("td", { class: "num" }, hasBudget ? formatMoney(c.remaining) : "–"),
      h("td", {}, hasBudget
        ? [h("progress", { class: c.over ? "over" : "", max: c.budgeted > 0 ? c.budgeted : 1, value: Math.max(0, c.budgeted > 0 ? Math.min(c.spent, c.budgeted) : c.over ? 1 : 0),
            "aria-label": `${c.categoryName}: spent ${formatMoney(c.spent)} of ${formatMoney(c.budgeted)}` }),
           c.over ? h("span", { class: "error" }, ` Over by ${formatMoney(-c.remaining)}`) : null]
        : h("span", { class: "muted" }, "No budget")));
  }

  clear(root,
    h("h1", {}, "Overview"),
    h("div", { class: "toolbar" }, field("Month", picker)),
    status.el, output);
  await load();
}
