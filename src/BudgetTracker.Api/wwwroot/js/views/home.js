import { clear, h } from "../dom.js";

export function homeView(root, { user }) {
  clear(root, h("h1", {}, "Overview"), h("p", {}, "Signed in as ", h("strong", {}, user.email), "."));
}
