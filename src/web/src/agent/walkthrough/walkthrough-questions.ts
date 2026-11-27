import type { AgentWalkthrough, WalkthroughCode } from "../../bridge";
import type { WalkthroughQuestion } from "../AgentPaneModel";

/** A question where the page draws it: its own place, or, once that is gone, its stop's end or the page's. */
export interface PlacedQuestion {
  question: WalkthroughQuestion;
  place: string;
  moved: boolean;
}

// A code place names the copy its line is numbered in, so a block recorded again doesn't move it to another line.
const copyOf = (code: WalkthroughCode): string => code.source.slice(0, 12);

/** The place of `line` (in the block's recorded copy) of block `index` of stop `stopId`. */
export const codePlace = (stopId: string, index: number, code: WalkthroughCode, line: number) =>
  `${stopId}/code/${index}/${line}@${copyOf(code)}`;

/** The copy's line a code place names in that block, or null for a place elsewhere. */
export function codeLine(place: string, stopId: string, index: number): number | null {
  const prefix = `${stopId}/code/${index}/`;
  return place.startsWith(prefix) ? Number.parseInt(place.slice(prefix.length), 10) : null;
}

/** Every place the page draws now. */
function places(walkthrough: AgentWalkthrough): Set<string> {
  const valid = new Set(["problem", "gist"]);
  if (walkthrough.closing) valid.add("closing");
  for (const stop of walkthrough.stops) {
    valid.add(`${stop.id}/lead`);
    if (stop.state) valid.add(`${stop.id}/state`);
    if (stop.why) valid.add(`${stop.id}/why`);
    if (stop.predict) valid.add(`${stop.id}/predict`);
    stop.code.forEach((code, index) => {
      for (let line = code.from; line <= code.to; line++)
        valid.add(codePlace(stop.id, index, code, line));
    });
  }
  return valid;
}

/** Places the questions asked about `walkthrough`. */
export function placeQuestions(
  questions: readonly WalkthroughQuestion[],
  walkthrough: AgentWalkthrough,
): PlacedQuestion[] {
  const valid = places(walkthrough);
  return questions.map((question) => {
    const place = question.anchor.place;
    if (valid.has(place)) return { question, place, moved: false };
    const stop = place.split("/")[0]!;
    return {
      question,
      place: walkthrough.stops.some((candidate) => candidate.id === stop) ? `${stop}/end` : "end",
      moved: true,
    };
  });
}

/** The quote an anchor keeps: the selection's first line, as long as an anchor allows. */
export const quoteOf = (text: string): string =>
  (text.split("\n").find((line) => line.trim() !== "") ?? text).trim().slice(0, 200);
