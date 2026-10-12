const KEY = "bt.token";

// sessionStorage: gone when the tab closes. It can throw (blocked storage), so the app also
// keeps the token in memory and simply asks you to sign in again after a reload.
let memoryToken = null;

export const session = {
  get token() {
    try {
      return sessionStorage.getItem(KEY) ?? memoryToken;
    } catch {
      return memoryToken;
    }
  },
  set(token) {
    memoryToken = token;
    try {
      sessionStorage.setItem(KEY, token);
    } catch {
      /* in-memory only */
    }
  },
  clear() {
    memoryToken = null;
    try {
      sessionStorage.removeItem(KEY);
    } catch {
      /* nothing stored */
    }
  },
};

export class ApiError extends Error {
  constructor(status, problem) {
    super(problem?.title ?? `Request failed (${status}).`);
    this.status = status;
    this.code = problem?.code ?? null;
    this.fields = problem?.errors ?? {};
    this.fieldCodes = problem?.codes ?? {};
  }
}

/** Fired when the server says the session is no longer valid. */
export const sessionExpired = new EventTarget();

export async function api(method, path, body, options = {}) {
  const headers = {};
  if (session.token) headers.authorization = `Bearer ${session.token}`;
  let payload;
  if (options.contentType) {
    headers["content-type"] = options.contentType;
    payload = body;
  } else if (body !== undefined) {
    headers["content-type"] = "application/json";
    payload = JSON.stringify(body);
  }

  const response = await fetch(path, { method, headers, body: payload });
  if (response.status === 204) return null;

  const isJson = (response.headers.get("content-type") ?? "").includes("json");
  const data = isJson ? await response.json() : null;
  if (!response.ok) {
    if (response.status === 401 && session.token && !options.expect401) {
      session.clear();
      sessionExpired.dispatchEvent(new Event("expired"));
    }
    throw new ApiError(response.status, data);
  }
  return data;
}

/** The message to show for a failure, with per-field messages joined on. */
export function describe(error) {
  if (!(error instanceof ApiError)) return "Something went wrong. Check your connection and try again.";
  const fields = Object.values(error.fields).flat();
  return fields.length > 0 ? fields.join(" ") : error.message;
}
