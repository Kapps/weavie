// A minimal CESP (OpenPeon) player: resolve the event's category in the active pack's openpeon.json, pick
// a random entry, play it at the configured volume — on the CLIENT, so a remote session's ping is heard
// locally. A failed pack load or a blocked play surfaces a keyed toast. See docs/specs/session-attention.md.

import type { AttentionKindName } from "../bridge";
import { notify } from "../notify/notify";
import { notificationPrefs } from "./prefs";

// AttentionKind → CESP sound category (the pack manifest's keys).
const CESP_CATEGORY: Record<AttentionKindName, string> = {
  turnComplete: "task.complete",
  needsInput: "input.required",
  failed: "task.error",
};

// The subset of a CESP openpeon.json manifest the player reads.
interface PackManifest {
  categories: Record<string, { sounds: { file: string }[] } | undefined>;
}

// One load per pack per page. A failed load is NOT cached — the entry is dropped so the next event
// retries (a transient fetch failure must not mute the rest of the session) and its keyed toast
// re-raises until a load succeeds.
const manifests = new Map<string, Promise<PackManifest | null>>();
// Audio elements cached per URL and warmed when the manifest resolves, so a ping never waits on a
// fetch + decode — the whole point is a timely sound.
const audioCache = new Map<string, HTMLAudioElement>();

function audioFor(url: string): HTMLAudioElement {
  let audio = audioCache.get(url);
  if (audio === undefined) {
    audio = new Audio(url);
    audio.preload = "auto";
    audioCache.set(url, audio);
  }
  return audio;
}

function loadManifest(pack: string): Promise<PackManifest | null> {
  let pending = manifests.get(pack);
  if (pending === undefined) {
    pending = fetch(`/sounds/${pack}/openpeon.json`)
      .then((response) => (response.ok ? (response.json() as Promise<PackManifest>) : null))
      .catch(() => null)
      .then((manifest) => {
        if (manifest === null) {
          manifests.delete(pack);
          notify(
            "warn",
            `Sound pack '${pack}' couldn't be loaded — this attention sound was muted.`,
            "attention-sound-pack",
          );
          return null;
        }
        for (const category of Object.values(manifest.categories)) {
          for (const sound of category?.sounds ?? []) {
            audioFor(`/sounds/${pack}/${sound.file}`);
          }
        }
        return manifest;
      });
    manifests.set(pack, pending);
  }
  return pending;
}

/** Plays the active pack's sound for an attention event (a random entry from its CESP category). */
export async function playAttentionSound(kind: AttentionKindName): Promise<void> {
  const prefs = notificationPrefs();
  const manifest = await loadManifest(prefs.soundPack);
  // CESP player semantics: silently skip a category the pack doesn't fill. Unreachable with the bundled
  // pack (it fills every mapped category); revisit when user-installed packs land.
  const entries = manifest?.categories[CESP_CATEGORY[kind]]?.sounds ?? [];
  const entry = entries[Math.floor(Math.random() * entries.length)];
  if (entry === undefined) {
    return;
  }
  const audio = audioFor(`/sounds/${prefs.soundPack}/${entry.file}`);
  try {
    audio.volume = prefs.volume / 100;
    audio.currentTime = 0;
    await audio.play();
  } catch (error) {
    // Autoplay policy (NotAllowedError: the browser refuses audio until the user first interacts with
    // the page) gets its actionable message; any other failure (a missing/undecodable file) is named as
    // itself rather than misdiagnosed as autoplay.
    const blocked = error instanceof Error && error.name === "NotAllowedError";
    notify(
      "warn",
      blocked
        ? "The browser blocked the attention sound — click anywhere in Weavie once to enable audio."
        : `The attention sound failed to play: ${String(error)}`,
      "attention-sound-blocked",
    );
  }
}
