import { Range } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/core/range";
import type {
  IModelDecoration,
  IModelDeltaDecoration,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model";
import {
  ModelDecorationOptions,
  type TextModel,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model/textModel";
import type { monaco } from "../monaco-setup";
import { reviewOwnsDecoration } from "./review-projection-decorations";

/** Read-through layout model; Monaco's hidden-range tracking never mutates the working model. */
export function createReviewProjectionModel(
  model: TextModel,
  ownerId: number,
  markers: readonly monaco.editor.IModelDeltaDecoration[],
): TextModel {
  const ranges = new Map<string, Range>();
  let nextId = 0;
  const owns = (value: { ownerId: number }) => reviewOwnsDecoration(ownerId, value);
  const decorations: IModelDecoration[] = markers.map((marker, index) => ({
    id: `review-projection-marker:${index}`,
    ownerId: 0,
    range: model.validateRange(marker.range),
    options: ModelDecorationOptions.createDynamic({
      description: "review projection",
      ...marker.options,
    }) as IModelDecoration["options"],
  }));
  const local: Partial<TextModel> = {
    deltaDecorations: (old: string[] | undefined, added: IModelDeltaDecoration[]) => {
      for (const id of old ?? []) {
        if (!ranges.delete(id)) throw new Error("Projection removed an unowned tracking range");
      }
      return added.map((decoration) => {
        if (decoration.options !== ModelDecorationOptions.EMPTY)
          throw new Error("Projection tried to mutate a visible model decoration");
        const id = `review-projection-range:${nextId++}`;
        ranges.set(id, Range.lift(decoration.range));
        return id;
      });
    },
    getDecorationRange: (id) => {
      const range = ranges.get(id);
      if (!range) throw new Error("Projection queried an unowned tracking range");
      return range;
    },
    getLineInjectedText: (line) => model.getLineInjectedText(line).filter(owns),
    getDecorationsInRange: (
      range,
      _queryOwner,
      filterValidation,
      filterFonts,
      onlyMinimap,
      onlyMargin,
    ) => {
      const actual = model
        .getDecorationsInRange(range, 0, filterValidation, filterFonts, onlyMinimap, onlyMargin)
        .filter(owns);
      return actual.concat(
        decorations.filter((decoration) => {
          if (!Range.areIntersectingOrTouching(decoration.range, range)) return false;
          if (onlyMinimap && !decoration.options.minimap) return false;
          if (onlyMargin && !decoration.options.glyphMarginClassName) return false;
          return true;
        }),
      );
    },
    getCustomLineHeightsDecorations: () => model.getCustomLineHeightsDecorations().filter(owns),
    dispose: () => {
      throw new Error("A review projection does not own the working model");
    },
  };
  const methods = new Map<PropertyKey, unknown>();
  return new Proxy(model, {
    get(target, key) {
      if (Object.hasOwn(local, key)) return Reflect.get(local, key);
      const value = Reflect.get(target, key, target);
      if (typeof value !== "function") return value;
      if (!methods.has(key)) methods.set(key, value.bind(target));
      return methods.get(key);
    },
    set() {
      throw new Error("A review projection cannot mutate the working model");
    },
  });
}
