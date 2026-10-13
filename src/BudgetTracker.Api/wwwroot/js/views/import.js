import { api, ApiError, describe } from "../api.js";
import { clear, formatMoney, h } from "../dom.js";
import { field, pill, statusLine, TRANSFER_TIP } from "../ui.js";

const SHOWN_ROWS = 500;
const DATE_FORMATS = [
  ["iso", "2026-10-31"], ["us", "10/31/2026"], ["eu", "31/10/2026"], ["isoslash", "2026/10/31"], ["eudots", "31.10.2026"],
];

export async function importView(root) {
  const status = statusLine();
  const [accounts, categories] = await Promise.all([api("GET", "/accounts"), api("GET", "/categories")]);
  const categoryName = new Map(categories.map((c) => [c.id, c.name]));
  const result = h("div", { "aria-live": "polite" });

  if (accounts.length === 0) {
    clear(root, h("h1", {}, "Import"), h("p", {}, "Create an account under Accounts and categories first."));
    return;
  }

  const account = h("select", { name: "account" }, accounts.map((a) => h("option", { value: a.id }, a.name)));
  const file = h("input", { type: "file", name: "file", accept: ".csv,text/csv" });
  const dateFormat = h("select", { name: "date-format" },
    DATE_FORMATS.map(([value, example]) => h("option", { value }, `${example} (${value})`)));
  const columns = Object.fromEntries(
    ["dateColumn", "descriptionColumn", "amountColumn", "debitColumn", "creditColumn"].map((name) => [name, h("input", { name })]));
  const preview = h("button", { type: "submit" }, "Preview");

  function url(dryRun) {
    const q = new URLSearchParams({ dateFormat: dateFormat.value });
    for (const [name, input] of Object.entries(columns)) if (input.value.trim()) q.set(name, input.value.trim());
    if (dryRun) q.set("dryRun", "true");
    return `/accounts/${account.value}/imports?${q}`;
  }

  const send = (dryRun) => api("POST", url(dryRun), file.files[0], { contentType: "text/csv" });

  const form = h("form", { class: "stack", onsubmit: async (event) => {
    event.preventDefault();
    status.clear();
    clear(result);
    if (file.files.length === 0) return status.error("Choose a CSV file first.");
    preview.disabled = true;
    try {
      showPreview(await send(true));
    } catch (e) {
      showFailure(e);
    } finally {
      preview.disabled = false;
    }
  } },
  field("Account", account),
  field("CSV file", file),
  field("Date format in the file", dateFormat),
  h("details", {}, h("summary", {}, "Column names (only if detection fails)"),
    h("div", { class: "stack" },
      field("Date column", columns.dateColumn), field("Description column", columns.descriptionColumn),
      field("Amount column", columns.amountColumn), field("Debit column", columns.debitColumn),
      field("Credit column", columns.creditColumn))),
  preview);

  function showFailure(e) {
    if (e instanceof ApiError && e.code === "import.invalid") {
      const problems = e.problem?.errors ?? [];
      clear(result,
        h("p", { class: "error", role: "alert" }, `${e.message} Fix these and preview again:`),
        h("ul", { "aria-label": "Problems in the file" },
          problems.map((p) => h("li", {}, p.row > 0 ? `Row ${p.row}: ` : "", p.message))),
        e.problem?.errorsTruncated ? h("p", { class: "muted" }, "More problems were found than are listed.") : null);
    } else {
      status.error(describe(e));
    }
  }

  function showPreview(summary) {
    const rows = summary.rows.slice(0, SHOWN_ROWS);
    const confirm = h("button", { type: "button", disabled: summary.new === 0, onclick: async () => {
      confirm.disabled = true;
      try {
        showDone(await send(false));
      } catch (e) {
        confirm.disabled = false;
        showFailure(e);
      }
    } }, summary.new === 0 ? "Nothing new to import" : `Import ${summary.new} new transaction${summary.new === 1 ? "" : "s"}`);

    clear(result,
      h("h2", {}, "Preview"),
      h("p", {}, `${summary.total} rows: `, h("strong", {}, `${summary.new} new`), `, ${summary.duplicates} already imported.`),
      summary.signInverted
        ? h("p", { class: "muted" }, "This account flips signs: the stored amount is the file's amount with its sign reversed.")
        : null,
      h("table", { "aria-label": "Preview of the import" },
        h("thead", {}, h("tr", {}, ["Row", "Date", "Description", "Category"].map((c) => h("th", {}, c)),
          h("th", { class: "num" }, "Stored amount"), h("th", { class: "num" }, "Amount in file"), h("th", {}, "Status"))),
        h("tbody", {}, rows.map((r) => h("tr", {},
          h("td", {}, r.row), h("td", {}, r.postedOn), h("td", {}, r.description),
          h("td", {}, r.isTransfer ? pill("transfer", TRANSFER_TIP) : categoryName.get(r.categoryId) ?? ""),
          h("td", { class: "num" }, formatMoney(r.amount)), h("td", { class: "num" }, formatMoney(r.fileAmount)),
          h("td", {}, r.status === "duplicate"
            ? pill(r.status, "Already imported earlier, so this row is skipped and nothing is added twice.")
            : pill(r.status, "Not imported before; this row will be added.")))))),
      summary.rows.length > rows.length ? h("p", { class: "muted" }, `Showing the first ${rows.length} rows.`) : null,
      confirm);
  }

  function showDone(summary) {
    clear(result,
      h("h2", {}, "Imported"),
      h("p", { role: "status" },
        `Imported ${summary.imported} transaction${summary.imported === 1 ? "" : "s"}`,
        summary.duplicates > 0 ? `, skipped ${summary.duplicates} already there` : "",
        summary.categorized > 0 ? `; ${summary.categorized} categorized by your rules` : "",
        summary.markedAsTransfer > 0 ? `; ${summary.markedAsTransfer} marked as transfers` : "", "."),
      h("p", {}, h("a", { href: "/ui/transactions" }, "View transactions")));
  }

  clear(root, h("h1", {}, "Import"), status.el, h("div", { class: "panel" }, form), result);
}
