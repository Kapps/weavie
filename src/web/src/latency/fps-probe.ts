import { log } from "../bridge";

// Maximum-demand frame-rate probe: animates a visible, compositor-driving element every frame
// for 3s and reports the best (min) and median frame interval. This forces ProMotion to ramp if
// it can — so it answers "can this WKWebView actually paint faster than 60Hz on this machine?"
// (min ~8.3ms => 120Hz reachable; floors at ~16.7ms => effectively 60Hz-capped).
export function runFpsProbe(): void {
  const box = document.createElement("div");
  box.style.cssText =
    "position:fixed;top:0;left:0;width:60px;height:60px;background:#4ec9b0;will-change:transform;z-index:99999;pointer-events:none";
  document.body.appendChild(box);

  const intervals: number[] = [];
  let last = 0;
  let x = 0;
  const start = performance.now();

  const tick = (t: number): void => {
    if (last !== 0) {
      intervals.push(t - last);
    }
    last = t;
    x = (x + 9) % 400;
    box.style.transform = `translateX(${x}px)`;
    if (t - start < 3000) {
      requestAnimationFrame(tick);
      return;
    }

    box.remove();
    const sorted = [...intervals].sort((a, b) => a - b);
    const min = sorted[0] ?? 0;
    const p50 = sorted[Math.floor(sorted.length / 2)] ?? 0;
    const hzMin = min > 0 ? Math.round(1000 / min) : 0;
    const hzP50 = p50 > 0 ? Math.round(1000 / p50) : 0;
    log(
      "info",
      `FPS-PROBE frames=${intervals.length} min=${min.toFixed(2)}ms(${hzMin}Hz) p50=${p50.toFixed(2)}ms(${hzP50}Hz)`,
    );
  };

  requestAnimationFrame(tick);
}
