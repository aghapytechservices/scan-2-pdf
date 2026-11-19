import "@fontsource-variable/bricolage-grotesque/standard.css";
import "@fontsource/atkinson-hyperlegible/400.css";
import "@fontsource/atkinson-hyperlegible/700.css";
import "@fontsource/cookie/latin-400.css"; // Buy me a coffee button
import "./styles.css";
import "./site.css";

// Hero demo: play the scan once on load; "Scan again" replays it.
const demo = document.querySelector<HTMLElement>(".demo");
const replay = document.getElementById("replay");

function play() {
  if (!demo) return;
  demo.classList.remove("run");
  void demo.offsetWidth; // restart CSS animations
  demo.classList.add("run");
}

if (demo) {
  play();
  replay?.addEventListener("click", play);
}

// Share band
const SITE = "https://scan2pdf.aghapyts.com";
document.getElementById("share-email")?.addEventListener("click", () => {
  const subject = "A free way to scan straight into Outlook";
  const body =
    "Hi,\n\nScan 2 PDF adds a Scan button to Outlook, so a page or a whole stack from the scanner goes straight " +
    "into the email as one PDF. It's free:\n\n" + SITE + "\n";
  window.location.href = `mailto:?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`;
});
const copyBtn = document.getElementById("share-copy");
copyBtn?.addEventListener("click", async () => {
  const label = copyBtn.textContent;
  try {
    await navigator.clipboard.writeText(SITE);
    copyBtn.textContent = "Link copied";
  } catch {
    copyBtn.textContent = SITE;
  }
  setTimeout(() => (copyBtn.textContent = label), 2500);
});
