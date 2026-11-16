// Feedback, review and share prompts for the task pane.
// Everything is remembered only in this browser's localStorage. Nothing is sent anywhere.

/** Microsoft Marketplace listing (reviews). Set once the add-in is approved; the review button stays hidden until then. */
export const REVIEW_URL = "";
/** Where "Send feedback" goes. An email address opens a new message; otherwise the GitHub issue form is used. */
export const FEEDBACK_EMAIL = "";
export const SITE_URL = "https://scan2pdf.aghapyts.com";
const ISSUE_URL = "https://github.com/aghapytechservices/scan-2-pdf/issues/new";

const KEY = "scan2pdf-feedback";
const FIRST_ASK_AFTER = 3; // successful attaches
const ASK_AGAIN_AFTER = 10;

type State = "waiting" | "done" | "never";
interface Store {
  attaches: number;
  state: State;
  nextAt: number;
}

function load(): Store {
  const fallback: Store = { attaches: 0, state: "waiting", nextAt: FIRST_ASK_AFTER };
  try {
    const raw = localStorage.getItem(KEY);
    return raw ? { ...fallback, ...JSON.parse(raw) } : fallback;
  } catch {
    return fallback;
  }
}

function save(s: Store) {
  try {
    localStorage.setItem(KEY, JSON.stringify(s));
  } catch {
    /* storage unavailable: prompts just won't be remembered */
  }
}

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

type Panel = "ask" | "up" | "down" | "share";
let card: HTMLElement;

function show(panel: Panel) {
  card.classList.remove("hidden");
  for (const p of ["ask", "up", "down", "share"] as Panel[]) {
    $(`fb-${p}`).classList.toggle("hidden", p !== panel);
  }
  card.scrollIntoView({ behavior: "smooth", block: "nearest" });
}

function hide() {
  card.classList.add("hidden");
}

function finish(state: State = "done") {
  const s = load();
  s.state = state;
  save(s);
}

function open(url: string) {
  window.open(url, "_blank", "noopener");
}

// ---------- Share ----------

const shareSubject = "A free way to scan straight into Outlook";
const shareBody =
  "Hi,\n\nI've been using Scan 2 PDF. It adds a Scan button to Outlook, so a page or a whole stack from the " +
  "scanner goes straight into the email as one PDF. It's free:\n\n" +
  SITE_URL +
  "\n";

function emailColleague() {
  window.location.href = `mailto:?subject=${encodeURIComponent(shareSubject)}&body=${encodeURIComponent(shareBody)}`;
}

async function copyLink(button: HTMLButtonElement) {
  const original = button.textContent;
  try {
    await navigator.clipboard.writeText(SITE_URL);
    button.textContent = "Link copied";
  } catch {
    // Clipboard API can be blocked in some Outlook hosts: fall back to a selectable prompt-free copy
    const input = document.createElement("input");
    input.value = SITE_URL;
    document.body.append(input);
    input.select();
    const ok = document.execCommand("copy");
    input.remove();
    button.textContent = ok ? "Link copied" : SITE_URL;
  }
  setTimeout(() => (button.textContent = original), 2500);
}

async function systemShare() {
  try {
    await navigator.share({ title: "Scan 2 PDF for Outlook", text: shareSubject, url: SITE_URL });
  } catch {
    /* cancelled or unavailable */
  }
}

// ---------- Feedback ----------

function sendFeedback() {
  const subject = "Scan 2 PDF feedback";
  const body = "What happened, and what did you expect?\n\n\n---\nOutlook version:\nScanner model:\n";
  if (FEEDBACK_EMAIL) {
    window.location.href = `mailto:${FEEDBACK_EMAIL}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`;
  } else {
    open(`${ISSUE_URL}?title=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`);
  }
}

// ---------- Public API ----------

/** Call after every successful attach. Shows the rating prompt when it's due. */
export function recordSuccessfulAttach() {
  const s = load();
  s.attaches += 1;
  const due = s.state === "waiting" && s.attaches >= s.nextAt;
  save(s);
  if (due) show("ask");
}

export function initFeedback() {
  card = $("feedback");
  const hasShare = typeof navigator.share === "function";
  $("fb-system-share").classList.toggle("hidden", !hasShare);
  if (!hasShare) $("fb-copy").parentElement!.classList.replace("grid-cols-2", "grid-cols-1");
  $("fb-review").classList.toggle("hidden", !REVIEW_URL);
  $("fb-up-text").textContent = REVIEW_URL
    ? "Glad it's helping. A short review helps other people find Scan 2 PDF."
    : "Glad it's helping. Know someone who'd find it useful?";

  // Rating prompt
  $("fb-good").addEventListener("click", () => show("up"));
  $("fb-bad").addEventListener("click", () => show("down"));
  $("fb-later").addEventListener("click", () => {
    const s = load();
    s.nextAt = s.attaches + ASK_AGAIN_AFTER;
    save(s);
    hide();
  });
  $("fb-never").addEventListener("click", () => {
    finish("never");
    hide();
  });

  // Positive path
  $("fb-review").addEventListener("click", () => {
    finish();
    open(REVIEW_URL);
  });
  $("fb-recommend").addEventListener("click", () => {
    finish();
    show("share");
  });
  $("fb-up-close").addEventListener("click", () => {
    finish();
    hide();
  });

  // Negative path
  $("fb-send").addEventListener("click", () => {
    finish();
    sendFeedback();
  });
  $("fb-down-close").addEventListener("click", () => {
    finish();
    hide();
  });

  // Share panel
  $("fb-email").addEventListener("click", emailColleague);
  $("fb-copy").addEventListener("click", (e) => copyLink(e.currentTarget as HTMLButtonElement));
  $("fb-system-share").addEventListener("click", systemShare);
  $("fb-share-close").addEventListener("click", hide);

  // Footer links: always available
  $("footer-share").addEventListener("click", () => show("share"));
  $("footer-feedback").addEventListener("click", () => show("down"));
}
