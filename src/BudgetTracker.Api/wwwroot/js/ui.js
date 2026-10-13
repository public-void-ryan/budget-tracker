import { h } from "./dom.js";

/** A labelled control. The label wraps the control, so tests and screen readers find it by name. */
export const field = (label, control) => h("label", {}, label, control);

/** A paragraph that announces its text when it changes. */
export function statusLine() {
  const el = h("p", { role: "status", "aria-live": "polite" });
  return {
    el,
    ok(text) { el.className = "ok"; el.textContent = text; },
    error(text) { el.className = "error"; el.setAttribute("role", "alert"); el.textContent = text; },
    clear() { el.className = ""; el.setAttribute("role", "status"); el.textContent = ""; },
  };
}

/**
 * A destructive button that needs a second click: the first changes it to "Confirm ...",
 * and it reverts after a few seconds so a stray later click cannot delete anything.
 */
export function confirmButton(label, confirmLabel, onConfirm, { ariaLabel } = {}) {
  let timer = null;
  const button = h("button", {
    type: "button", class: "danger", "aria-label": ariaLabel,
    onclick: async () => {
      if (timer === null) {
        button.textContent = confirmLabel;
        button.removeAttribute("aria-label");
        timer = setTimeout(reset, 5000);
        return;
      }
      reset();
      await onConfirm();
    },
  }, label);
  function reset() {
    clearTimeout(timer);
    timer = null;
    button.textContent = label;
    if (ariaLabel) button.setAttribute("aria-label", ariaLabel);
  }
  return button;
}

const AMOUNT = /^-?\d+(\.\d{1,4})?$/;

/** Parses what a person typed as an amount; null if it is not a plain decimal with at most 4 places. */
export function parseAmount(text) {
  const trimmed = text.trim();
  return AMOUNT.test(trimmed) ? Number(trimmed) : null;
}

export const today = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
};

/** The month the Overview and Budgets screens show, kept while you move between them. */
let selectedMonth = null;
export const currentMonth = () => today().slice(0, 7);
export const getMonth = () => selectedMonth ?? currentMonth();
export const setMonth = (month) => { selectedMonth = month; };

export function previousMonth(month) {
  const [year, m] = month.split("-").map(Number);
  return m === 1 ? `${year - 1}-12` : `${year}-${String(m - 1).padStart(2, "0")}`;
}

/** A month picker that calls `onChange` with a valid yyyy-MM (empty or partial input is ignored). */
export function monthPicker(onChange) {
  const input = h("input", { type: "month", name: "month", value: getMonth(), required: true });
  input.addEventListener("change", () => {
    if (/^\d{4}-\d{2}$/.test(input.value)) {
      setMonth(input.value);
      onChange(input.value);
    }
  });
  return input;
}

/**
 * A small label with a plain-language explanation: the tooltip for mouse users, and the same text
 * hidden-but-readable for screen readers (a title attribute alone is not reliably announced).
 */
export function pill(text, explanation) {
  return h("span", { class: "pill explained", title: explanation },
    text, h("span", { class: "sr-only" }, ` (${explanation})`));
}

export const TRANSFER_TIP = "Moves money between your own accounts, so it is not counted as spending or income.";
