import { createEffect, type JSX } from "solid-js";
import type { EditorController } from "../editor-controller";
import type { EditorHost } from "../editor-host";
import type { TabOwner } from "../tab-owner";
import type { UnifiedReviewSurface } from "./review-surface";
import UnifiedReview from "./UnifiedReview";

/** The review owns its section models; navigation and persistence belong to its tab. */
export default function ReviewTab(props: {
  tab: TabOwner;
  controller: EditorController;
  host: EditorHost;
}): JSX.Element {
  const { tab, controller } = props;
  const overview = () => controller.review.overviewFor(tab.session);
  const requested = new Set<string>();
  createEffect(() => {
    const files = overview().files;
    const live = new Set(files.map((file) => file.summary().path));
    for (const path of requested) if (!live.has(path)) requested.delete(path);
    for (const file of files) {
      const path = file.summary().path;
      if (!file.loaded() && !requested.has(path)) {
        requested.add(path);
        tab.session.feature("review").publish("showFile", { path });
      }
    }
  });
  const bind = (surface: UnifiedReviewSurface): (() => void) => {
    const unregister = tab.mount(surface);
    return () => {
      controller.captureTab(tab);
      unregister();
    };
  };
  return (
    <UnifiedReview
      tab={tab}
      session={tab.session}
      scope={controller.review.scope}
      overview={overview}
      changed={() => controller.captureTab(tab)}
      bindSurface={bind}
      clear={props.host.clear}
      onFileCollapsed={controller.review.setFileCollapsed}
      configureDiff={controller.review.configureDiff}
      createCopyScope={() => props.host.createReviewCopyScope(tab.session)}
    />
  );
}
