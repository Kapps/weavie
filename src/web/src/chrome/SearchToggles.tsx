import { CaseSensitive, EyeOff, Regex, WholeWord } from "lucide-solid";
import { For, type JSX } from "solid-js";
import { keyHint } from "../commands/key-hint";
import { CommandIds } from "../commands/types";
import type { SearchOptions } from "./search-model";

type OptionKey = "caseSensitive" | "wholeWord" | "regex" | "excludeGitignored";

const TOGGLES: {
  key: OptionKey;
  label: string;
  command: string;
  icon: () => JSX.Element;
}[] = [
  {
    key: "caseSensitive",
    label: "Match case",
    command: CommandIds.searchToggleMatchCase,
    icon: () => <CaseSensitive />,
  },
  {
    key: "wholeWord",
    label: "Whole word",
    command: CommandIds.searchToggleWholeWord,
    icon: () => <WholeWord />,
  },
  {
    key: "regex",
    label: "Use regular expression",
    command: CommandIds.searchToggleRegex,
    icon: () => <Regex />,
  },
  {
    key: "excludeGitignored",
    label: "Exclude gitignored files",
    command: CommandIds.searchToggleGitignore,
    icon: () => <EyeOff />,
  },
];

/** The match-option toggle cluster in the search input row; each button advertises its catalog binding. */
export function SearchToggles(props: {
  options: SearchOptions;
  onToggle: (key: OptionKey) => void;
}): JSX.Element {
  return (
    <span class="search-toggles">
      <For each={TOGGLES}>
        {(t) => (
          <button
            type="button"
            class="search-toggle"
            classList={{ active: props.options[t.key] }}
            aria-pressed={props.options[t.key]}
            title={`${t.label}${keyHint(t.command)}`}
            onClick={() => props.onToggle(t.key)}
          >
            {t.icon()}
          </button>
        )}
      </For>
    </span>
  );
}
