import { expect, it, vi } from "vitest";

const home = vi.hoisted(() => ({
  hello: (_hello: { home: { deviceSettings: Record<string, unknown> } }): void => {},
  push: new Map<string, (value: unknown) => void>(),
}));

vi.mock("./bridge", () => ({
  hostInjected: (_name: string, value: unknown) => value,
  registerHomeFeature: (install: (connection: unknown) => void) =>
    install({
      onInitializing: (listener: typeof home.hello) => {
        home.hello = listener;
        return () => {};
      },
      host: {
        feature: () => ({
          on: (name: string, handler: (value: unknown) => void) => {
            home.push.set(name, handler);
            return () => {};
          },
        }),
      },
    }),
}));

const { deviceSetting } = await import("./device-settings");

it("starts injected, then follows the home's pushes and the changes a hello reports", () => {
  const fonts = deviceSetting("fonts", "__FONTS__", { size: 13 }, { size: 0 });
  const seen: number[] = [];
  fonts.onChanged((value) => seen.push(value.size));
  expect(fonts.current()).toEqual({ size: 13 });

  home.hello({ home: { deviceSettings: { fonts: { size: 13 } } } });
  expect(seen).toEqual([]);

  home.push.get("fonts")?.({ size: 15 });
  home.hello({ home: { deviceSettings: { fonts: { size: 17 } } } });
  expect(seen).toEqual([15, 17]);
  expect(fonts.current()).toEqual({ size: 17 });
});
