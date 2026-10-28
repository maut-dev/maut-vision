import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import { UnsavedChangesProvider } from "./components/unsaved-changes";
import { installErrorReporting } from "./lib/error-reporting";
import "./index.css";

installErrorReporting();
createRoot(document.getElementById("root")!).render(<StrictMode><UnsavedChangesProvider><App /></UnsavedChangesProvider></StrictMode>);
