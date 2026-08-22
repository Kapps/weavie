const LINUX_NORMALIZATION = 5;

export function wheelScrollSensitivity(preference: number, platform: string | undefined): number {
  return preference * (platform === "linux" ? LINUX_NORMALIZATION : 1);
}
