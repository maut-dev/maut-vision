// Checks the UI catalogs (ui/src/locales/<language>/<namespace>.json):
//   - every language has the same namespaces and the same keys (plural forms are compared by their base key);
//   - a key has the same {{placeholders}} in every language; Russian plurals have one/few/many, English one/other;
//   - every error code the host can send (C# under src/, the UI bridge) has a text in the errors namespace;
//   - keys of the errors namespace that look like codes nobody sends are reported as warnings.
// Run: npm run check-locales (also part of npm run typecheck). Exit code 1 on an error.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

type Tree = { [key: string]: string | Tree };

const ui = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const repository = path.resolve(ui, "..");
const localesRoot = path.join(ui, "src", "locales");
const languages = fs.readdirSync(localesRoot).filter(name => fs.statSync(path.join(localesRoot, name)).isDirectory()).sort();
const pluralSuffix = /_(zero|one|two|few|many|other)$/;
const requiredPlurals: Record<string, string[]> = { ru: ["one", "few", "many"], en: ["one", "other"] };

const errors: string[] = [];
const warnings: string[] = [];

function flatten(tree: Tree, prefix = "", into = new Map<string, string>()): Map<string, string> {
    for (const [key, value] of Object.entries(tree)) {
        const name = prefix ? `${prefix}.${key}` : key;
        if (typeof value === "string") into.set(name, value);
        else flatten(value, name, into);
    }
    return into;
}

function placeholders(text: string): string {
    return [...text.matchAll(/\{\{\s*([\w.]+)[^}]*\}\}/g)].map(match => match[1]).filter(name => name !== "count").sort().join(",");
}

const catalogs = new Map<string, Map<string, Map<string, string>>>();
for (const language of languages) {
    const namespaces = new Map<string, Map<string, string>>();
    for (const file of fs.readdirSync(path.join(localesRoot, language)).filter(name => name.endsWith(".json"))) {
        const tree = JSON.parse(fs.readFileSync(path.join(localesRoot, language, file), "utf8")) as Tree;
        namespaces.set(path.basename(file, ".json"), flatten(tree));
    }
    catalogs.set(language, namespaces);
}

// The same namespaces and keys in every language.
const reference = languages.includes("ru") ? "ru" : languages[0];
const referenceCatalog = catalogs.get(reference)!;
for (const language of languages) {
    const catalog = catalogs.get(language)!;
    for (const namespace of new Set([...referenceCatalog.keys(), ...catalog.keys()])) {
        const own = catalog.get(namespace);
        const base = referenceCatalog.get(namespace);
        if (!own || !base) {
            errors.push(`Namespace ${namespace} is missing in ${own ? reference : language}.`);
            continue;
        }
        if (language === reference) continue;
        const ownBase = baseKeys(own);
        const referenceBase = baseKeys(base);
        for (const key of referenceBase.keys()) if (!ownBase.has(key)) errors.push(`${language}/${namespace}: missing key ${key}.`);
        for (const key of ownBase.keys()) if (!referenceBase.has(key)) errors.push(`${language}/${namespace}: extra key ${key} (not in ${reference}).`);
        for (const [key, text] of referenceBase) {
            const other = ownBase.get(key);
            if (other !== undefined && placeholders(text) !== placeholders(other))
                errors.push(`${language}/${namespace}: ${key} has placeholders {${placeholders(other)}}, ${reference} has {${placeholders(text)}}.`);
        }
    }
}

// Plural forms each language needs.
for (const language of languages) {
    const required = requiredPlurals[language];
    if (!required) continue;
    for (const [namespace, keys] of catalogs.get(language)!) {
        const forms = new Map<string, Set<string>>();
        for (const key of keys.keys()) {
            const match = key.match(pluralSuffix);
            if (match) forms.set(key.replace(pluralSuffix, ""), (forms.get(key.replace(pluralSuffix, "")) ?? new Set()).add(match[1]));
        }
        for (const [key, have] of forms) {
            const lacking = required.filter(form => !have.has(form));
            if (lacking.length) errors.push(`${language}/${namespace}: plural ${key} lacks ${lacking.map(form => "_" + form).join(", ")}.`);
        }
    }
}

/** Keys with plural suffixes folded into their base; the text of the first form represents the key. */
function baseKeys(keys: Map<string, string>): Map<string, string> {
    const result = new Map<string, string>();
    for (const [key, text] of keys) {
        const base = key.replace(pluralSuffix, "");
        if (!result.has(base)) result.set(base, text);
    }
    return result;
}

// Error codes of the host. Areas are the Core blocks and the transport; a literal "<area>.<reason>" in lower snake_case is a code unless
// it is a job kind or a file name.
const areas = ["projects", "base_models", "jobs", "images", "backgrounds", "video", "variants", "classes", "generation", "datasets", "training",
    "checks", "screen", "hardware", "bridge", "resources"];
// CatalogContext, CatalogSql, CatalogDeletion and NamedRows throw $"{area}.<reason>" for these areas.
const catalogAreas = ["variants", "classes"];
// Worker outcomes get the prefix of their consumer ("training." + code, "checks." + code); artifacts come from training runs only.
const workerAreas = ["training", "checks"];
const fileExtensions = new Set(["json", "jsonl", "yaml", "yml", "db", "pt", "onnx", "txt", "md", "log", "exe", "dll", "py", "zip", "png"]);

function sourceFiles(directory: string, extension: string): string[] {
    const result: string[] = [];
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
        if (entry.name === "bin" || entry.name === "obj" || entry.name === "node_modules") continue;
        const full = path.join(directory, entry.name);
        if (entry.isDirectory()) result.push(...sourceFiles(full, extension));
        else if (entry.name.endsWith(extension)) result.push(full);
    }
    return result;
}

const csharp = sourceFiles(path.join(repository, "src"), ".cs")
    // Routes and event names look like codes; smoke checks only read codes.
    .filter(file => !/SmokeRunner|ControllerRegistration\.cs$/.test(file) && !file.includes(`${path.sep}Events${path.sep}`));
const jobKinds = new Set<string>();
for (const file of csharp) {
    const text = fs.readFileSync(file, "utf8");
    for (const match of text.matchAll(/\[JobKind\("([^"]+)"\)\]/g)) jobKinds.add(match[1]);
    for (const match of text.matchAll(/Kind\s*=\s*"([^"]+)"/g)) jobKinds.add(match[1]);
}
const sent = new Map<string, string>();
const literal = new RegExp(`"((?:${areas.join("|")})\\.([a-z][a-z0-9_]*))"`, "g");
for (const file of csharp) {
    const text = fs.readFileSync(file, "utf8");
    const where = path.relative(repository, file);
    for (const match of text.matchAll(literal)) {
        if (jobKinds.has(match[1]) || fileExtensions.has(match[2])) continue;
        if (!sent.has(match[1])) sent.set(match[1], where);
    }
    for (const match of text.matchAll(/\$"\{area\}\.([a-z][a-z0-9_]*)"/g)) {
        for (const area of catalogAreas) if (!sent.has(`${area}.${match[1]}`)) sent.set(`${area}.${match[1]}`, where);
    }
    if (file.includes(`${path.sep}Python${path.sep}`)) {
        for (const match of text.matchAll(/"((?:worker|protocol|result|runtime|artifact)_[a-z_]+)"/g)) {
            for (const area of match[1].startsWith("artifact_") ? ["training"] : workerAreas) {
                if (!sent.has(`${area}.${match[1]}`)) sent.set(`${area}.${match[1]}`, where);
            }
        }
    }
}
// Errors the UI bridge raises itself.
for (const file of sourceFiles(path.join(ui, "src"), ".ts")) {
    const text = fs.readFileSync(file, "utf8");
    for (const match of text.matchAll(/new BridgeError\("([a-z_]+\.[a-z0-9_]+)"/g)) {
        if (!sent.has(match[1])) sent.set(match[1], path.relative(repository, file));
    }
}
// Codes of python/worker/protocol/errors.py: shown with the worker's own message, a text of the code is optional.
const workerCodes = new Set<string>();
const workerErrors = path.join(repository, "python", "worker", "protocol", "errors.py");
if (fs.existsSync(workerErrors)) {
    for (const match of fs.readFileSync(workerErrors, "utf8").matchAll(/^\s+[A-Z_]+ = "([a-z_]+)"$/gm)) {
        for (const area of workerAreas) workerCodes.add(`${area}.worker_${match[1]}`);
    }
}

// Line texts built from a code (deletion blockers, plan warnings, skip reasons) also cover it.
const lineTrees = ["blocker", "planWarning", "backgroundReason"];
for (const language of languages) {
    const keys = baseKeys(catalogs.get(language)!.get("errors") ?? new Map());
    const covered = new Set<string>();
    for (const key of keys.keys()) {
        const [tree] = key.split(".", 1);
        if (!lineTrees.includes(tree)) covered.add(key);
        else {
            const parts = key.split(".");
            for (let start = 1; start < parts.length - 1; start++) covered.add(parts.slice(start).join("."));
        }
    }
    for (const [code, where] of sent) if (!covered.has(code)) errors.push(`${language}/errors: no text for ${code} (${where}).`);
    if (language === reference) {
        for (const key of keys.keys()) {
            const [area] = key.split(".", 1);
            if (areas.includes(area) && !sent.has(key) && !workerCodes.has(key)) warnings.push(`errors: ${key} is not sent by the host or the UI.`);
        }
    }
}

for (const warning of warnings) console.warn(`warning: ${warning}`);
for (const error of errors) console.error(`error: ${error}`);
const total = [...referenceCatalog.values()].reduce((sum, keys) => sum + baseKeys(keys).size, 0);
console.log(`Locales ${languages.join(", ")}: ${referenceCatalog.size} namespaces, ${total} keys, ${sent.size} error codes; ${errors.length} errors, ${warnings.length} warnings.`);
process.exit(errors.length ? 1 : 0);
