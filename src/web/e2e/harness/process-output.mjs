// Capture each startup field only from complete stdout lines; stderr is diagnostic output.
export function waitForOutputLines(proc, patterns, timeoutMs) {
  return new Promise((resolve, reject) => {
    let pending = "";
    let log = "";
    const values = patterns.map(() => null);
    const finish = () => {
      clearTimeout(timer);
      proc.stdout.off("data", onData);
      proc.stderr.off("data", onErrorData);
      proc.off("close", onClose);
      proc.off("error", onError);
    };
    const onError = (error) => {
      finish();
      reject(error);
    };
    const onErrorData = (chunk) => {
      log += chunk.toString("utf8");
    };
    const onData = (chunk) => {
      const text = chunk.toString("utf8");
      log += text;
      pending += text;
      const lines = pending.split("\n");
      pending = lines.pop();
      for (const line of lines) {
        for (const [index, pattern] of patterns.entries()) {
          const match = line.match(pattern);
          if (match) values[index] = match[1];
        }
        if (values.every((value) => value !== null)) {
          finish();
          resolve(values);
          return;
        }
      }
    };
    const onClose = (code) => onError(new Error(`host exited early with code ${code}:\n${log}`));
    const timer = setTimeout(
      () => onError(new Error(`host did not report startup fields in time:\n${log}`)),
      timeoutMs,
    );
    proc.stdout.on("data", onData);
    proc.stderr.on("data", onErrorData);
    proc.once("close", onClose);
    proc.once("error", onError);
  });
}
