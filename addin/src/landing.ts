import "@fontsource-variable/bricolage-grotesque/standard.css";
import "@fontsource/atkinson-hyperlegible/400.css";
import "@fontsource/atkinson-hyperlegible/700.css";
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
