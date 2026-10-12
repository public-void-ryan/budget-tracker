import { api, describe } from "../api.js";
import { clear, h } from "../dom.js";
import { confirmButton, field, statusLine } from "../ui.js";

const MATCH_TYPES = [["Contains", "contains"], ["StartsWith", "starts with"], ["Equals", "equals"]];

export async function rulesView(root) {
  const status = statusLine();
  const categories = await api("GET", "/categories");
  const categoryName = new Map(categories.map((c) => [c.id, c.name]));
  const list = h("ol", { "aria-label": "Rules in the order they are tried" });
  const tryResult = h("p", { "aria-live": "polite" });
  const applyResult = h("div", { "aria-live": "polite" });
  let rules = [];

  async function refresh() {
    rules = await api("GET", "/rules");
    clear(list, rules.length === 0 ? h("li", { class: "muted" }, "No rules yet.") : rules.map(item));
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

  const label = (r) => [
    MATCH_TYPES.find(([value]) => value === r.matchType)?.[1] ?? r.matchType, " “", h("strong", {}, r.pattern), "” ",
    "→ ",
    r.categoryId ? categoryName.get(r.categoryId) ?? "unknown category" : null,
    r.categoryId && r.markTransfer ? " and " : null,
    r.markTransfer ? "mark as transfer" : null,
  ];

  function item(r, index) {
    const move = (to) => {
      const ids = rules.map((x) => x.id);
      ids.splice(index, 1);
      ids.splice(to, 0, r.id);
      return run(() => api("POST", "/rules/order", { ids }));
    };
    return h("li", {},
      label(r), " ",
      h("button", { type: "button", class: "secondary", disabled: index === 0, "aria-label": `Move rule ${r.pattern} up`, onclick: () => move(index - 1) }, "Up"), " ",
      h("button", { type: "button", class: "secondary", disabled: index === rules.length - 1, "aria-label": `Move rule ${r.pattern} down`, onclick: () => move(index + 1) }, "Down"), " ",
      confirmButton("Delete", "Confirm delete", () => run(() => api("DELETE", `/rules/${r.id}`), "Rule deleted."),
        { ariaLabel: `Delete rule ${r.pattern}` }));
  }

  // ---- add ----
  const pattern = h("input", { name: "pattern", required: true, maxlength: 200 });
  const matchType = h("select", { name: "match-type" }, MATCH_TYPES.map(([value, text]) => h("option", { value }, text)));
  const category = h("select", { name: "category" }, h("option", { value: "" }, "No category"),
    categories.map((c) => h("option", { value: c.id }, c.name)));
  const transfer = h("input", { type: "checkbox", name: "mark-transfer" });
  const addForm = h("form", { class: "row", onsubmit: (event) => {
    event.preventDefault();
    run(async () => {
      await api("POST", "/rules", {
        pattern: pattern.value, matchType: matchType.value,
        categoryId: category.value || null, markTransfer: transfer.checked,
      });
      pattern.value = "";
    }, "Rule added at the end of the list.");
  } },
  field("When the description", matchType), field("Text", pattern),
  field("Set category", category), h("label", {}, "Mark as transfer", transfer),
  h("button", { type: "submit" }, "Add rule"));

  // ---- try ----
  const sample = h("input", { name: "sample", maxlength: 500 });
  const tryForm = h("form", { class: "row", onsubmit: async (event) => {
    event.preventDefault();
    clear(tryResult);
    try {
      const m = await api("GET", `/rules/match?${new URLSearchParams({ description: sample.value })}`);
      clear(tryResult, m.matched ? ["The first matching rule: ", label(m.rule)] : "No rule matches.");
    } catch (e) {
      status.error(describe(e));
    }
  } }, field("Try a description", sample), h("button", { type: "submit", class: "secondary" }, "Test"));

  // ---- apply ----
  async function apply(dryRun) {
    status.clear();
    clear(applyResult);
    try {
      const r = await api("POST", `/rules/apply${dryRun ? "?dryRun=true" : ""}`);
      clear(applyResult,
        h("p", {}, dryRun ? "Would change " : "Changed ",
          h("strong", {}, `${r.categorized} categorized`), `, ${r.markedAsTransfer} marked as transfer, `,
          `out of ${r.examined} uncategorized transactions.`,
          r.skippedArchived > 0 ? ` ${r.skippedArchived} in archived accounts were left alone.` : ""),
        r.byRule.length > 0
          ? h("ul", { "aria-label": "Matches per rule" }, r.byRule.map((hit) => h("li", {}, `“${hit.pattern}”: ${hit.matched}`)))
          : null);
    } catch (e) {
      status.error(describe(e));
    }
  }

  clear(root,
    h("h1", {}, "Rules"),
    h("p", { class: "muted" }, "The first rule that matches a transaction’s description decides its category. Rules only fill in blanks; they never change a category you chose."),
    status.el,
    h("div", { class: "panel" }, list),
    h("div", { class: "panel" }, h("h2", {}, "Add a rule"), addForm),
    h("div", { class: "panel" }, h("h2", {}, "Test"), tryForm, tryResult),
    h("div", { class: "panel" },
      h("h2", {}, "Apply to existing transactions"),
      h("div", { class: "row" },
        h("button", { type: "button", class: "secondary", onclick: () => apply(true) }, "Preview changes"),
        h("button", { type: "button", onclick: () => apply(false) }, "Apply rules")),
      applyResult));

  await refresh();
}
