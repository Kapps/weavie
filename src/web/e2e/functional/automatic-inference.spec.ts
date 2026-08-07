import { allowAutomaticInference } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test.use({ automaticInference: false, dismissInferenceOffer: false });

test("offers and enables automatic inference on desktop", async ({ page }) => {
  expect(page.viewportSize()).toEqual({ width: 1280, height: 800 });
  await allowAutomaticInference(page);
});
