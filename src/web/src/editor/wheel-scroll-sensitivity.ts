const LINUX_NORMALIZATION = 3;

/** The wheel options every Monaco scroller takes from Weavie's editor settings. */
export function wheelOptions(options: {
  mouseWheelScrollSensitivity: number;
  fastScrollSensitivity: number;
}): { mouseWheelScrollSensitivity: number; fastScrollSensitivity: number } {
  return {
    mouseWheelScrollSensitivity: wheelScrollSensitivity(
      options.mouseWheelScrollSensitivity,
      window.__WEAVIE_SHELL__?.platform,
    ),
    fastScrollSensitivity: options.fastScrollSensitivity,
  };
}

export function wheelScrollSensitivity(preference: number, platform: string | undefined): number {
  return preference * (platform === "linux" ? LINUX_NORMALIZATION : 1);
}
