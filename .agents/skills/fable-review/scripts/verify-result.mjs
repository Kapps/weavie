process.stdin.setEncoding("utf8");

let input = "";
for await (const chunk of process.stdin) {
  input += chunk;
}

let response;
try {
  response = JSON.parse(input);
} catch (error) {
  console.error(`Claude Code returned invalid JSON: ${error.message}`);
  process.exit(1);
}

if (
  response === null ||
  typeof response !== "object" ||
  response.type !== "result" ||
  response.subtype !== "success" ||
  response.is_error
) {
  const detail =
    response !== null && typeof response === "object" && typeof response.result === "string"
      ? response.result
      : JSON.stringify(response);
  console.error(`Fable review failed: ${detail}`);
  process.exit(1);
}

const models = Object.entries(response.modelUsage ?? {});
// Agent/Task is not exposed, so a Fable entry with output can only be the primary reviewer. Claude Code may
// still report small auxiliary-model usage of its own.
const fable = models.find(
  ([model, usage]) => model.startsWith("claude-fable-") && Number(usage.outputTokens) > 0,
);
if (fable === undefined) {
  console.error(
    `Fable model verification failed; result reported: ${models.length > 0 ? models.map(([model]) => model).join(", ") : "no models"}`,
  );
  process.exit(1);
}

const fableOutput = Number(fable[1].outputTokens);
const otherOutput = Math.max(
  0,
  ...models
    .filter(([model]) => !model.startsWith("claude-fable-"))
    .map(([, usage]) => Number(usage.outputTokens) || 0),
);
if (fableOutput <= otherOutput) {
  console.error(
    `Fable model verification failed; Fable produced ${fableOutput} output tokens versus ${otherOutput} from another model`,
  );
  process.exit(1);
}

if (Array.isArray(response.permission_denials) && response.permission_denials.length > 0) {
  console.error(
    `Fable review had permission denials: ${JSON.stringify(response.permission_denials)}`,
  );
  process.exit(1);
}

if (typeof response.result !== "string" || response.result.trim().length === 0) {
  console.error("Fable review returned no text.");
  process.exit(1);
}

console.error(`[fable-review] verified ${fable[0]}`);
console.log(response.result);
