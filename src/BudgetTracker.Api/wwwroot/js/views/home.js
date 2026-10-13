import { api, describe } from "../api.js";
import { clear, formatMoney, h } from "../dom.js";
import { field, getMonth, monthPicker, statusLine } from "../ui.js";

export async function homeView(root) {
  const status = statusLine();
  const output = h("div", { "aria-live": "polite" });
  const picker = monthPicker(load);

  // Only the latest request may draw, so a slow answer for a month you have left cannot overwrite the current one.
  let latest = 0;

  async function load(month = getMonth()) {
    const mine = ++latest;
    status.clear();
    output.setAttribute("aria-busy", "true");
    try {
      const summary = await api("GET", `/budgets/summary?month=${month}`);
      if (mine === latest) show(summary);
    } catch (e) {
      if (mine === latest) {
        clear(output);
        status.error(describe(e));
      }
    } finally {
      if (mine === latest) output.setAttribute("aria-busy", "false");
    }
  }

  function show(s) {
    if (s.categories.length === 0 && s.income.length === 0 && s.uncategorizedSpent === 0 && s.uncategorizedReceived === 0) {
      clear(output, h("p", { class: "muted" }, "Nothing to show for this month. Import transactions or set budgets to see your spending here."));
      return;
    }
    const t = s.totals;
    clear(output,
      h("h2", {}, "Spending"),
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
          h("th", { class: "num" }, formatMoney(t.budgeted)), h("th", { class: "num" }, formatMoney(t.spent)),
          h("th", { class: "num" }, formatMoney(t.remaining)), h("th")))),
      s.income.length > 0 || s.uncategorizedReceived !== 0
        ? [
          h("h2", {}, "Income"),
          h("table", { "aria-label": "Income received" },
            h("thead", {}, h("tr", {}, h("th", {}, "Category"), h("th", { class: "num" }, "Received"))),
            h("tbody", {},
              s.income.map((i) => h("tr", {}, h("td", {}, i.categoryName), h("td", { class: "num" }, formatMoney(i.received)))),
              s.uncategorizedReceived !== 0
                ? h("tr", {}, h("td", {}, "Uncategorized"), h("td", { class: "num" }, formatMoney(s.uncategorizedReceived)))
                : null),
            h("tfoot", {}, h("tr", {}, h("th", {}, "Total"), h("th", { class: "num" }, formatMoney(t.received))))),
        ]
        : null,
      h("p", { class: "net" }, "Net for the month: ", h("strong", {}, formatMoney(t.net)),
        h("span", { class: "muted" }, ` (${formatMoney(t.received)} received − ${formatMoney(t.spent)} spent)`)),
      h("p", { class: "muted" }, "Spent is money out minus refunds; transfers are left out. Money in to a category with no budget counts as income."));
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
