import { api, describe, session } from "../api.js";
import { clear, h } from "../dom.js";

/** Login and registration on one screen. `onSignedIn` runs once a session exists. */
export function authView(root, { onSignedIn }) {
  let registering = false;
  render();

  function render(message = "") {
    const email = h("input", { id: "email", name: "email", type: "email", autocomplete: "email", required: true });
    const password = h("input", {
      id: "password", name: "password", type: "password", required: true,
      autocomplete: registering ? "new-password" : "current-password",
    });
    const error = h("p", { class: "error", role: "alert" }, message);
    const submit = h("button", { type: "submit" }, registering ? "Create account" : "Log in");

    const form = h(
      "form",
      {
        class: "stack",
        novalidate: true,
        onsubmit: async (event) => {
          event.preventDefault();
          submit.disabled = true;
          try {
            const credentials = { email: email.value, password: password.value };
            if (registering) await api("POST", "/auth/register", credentials, { expect401: true });
            const login = await api("POST", "/auth/login", credentials, { expect401: true });
            session.set(login.token);
            await onSignedIn();
          } catch (e) {
            submit.disabled = false;
            clear(error, e.status === 401 ? "Email or password is incorrect." : describe(e));
          }
        },
      },
      h("label", {}, "Email", email),
      h("label", {}, "Password", password),
      registering ? h("p", { class: "muted" }, "At least 12 characters; not a common password.") : null,
      error,
      submit,
    );

    clear(
      root,
      h("h1", {}, registering ? "Create an account" : "Log in"),
      h("div", { class: "panel" }, form),
      h(
        "p",
        {},
        registering ? "Already have an account? " : "New here? ",
        h("button", { type: "button", class: "link", onclick: () => { registering = !registering; render(); } },
          registering ? "Log in" : "Create an account"),
      ),
    );
    email.focus();
  }
}
