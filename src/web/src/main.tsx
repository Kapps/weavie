import { render } from "solid-js/web";
import App from "./App";
import { postToHost } from "./bridge";
import "./styles.css";

const root = document.getElementById("root");
if (root === null) {
  throw new Error("missing #root");
}

postToHost({ type: "ready" });
render(() => <App />, root);
