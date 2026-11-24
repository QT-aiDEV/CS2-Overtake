import assert from "node:assert/strict";
import fs from "node:fs";
import Module from "node:module";
import { createRequire } from "node:module";
import React from "react";
import ts from "typescript";

const require = createRequire(import.meta.url);
function loadTypescript(path) {
    const compiled = ts.transpileModule(fs.readFileSync(path, "utf8"), {
        compilerOptions: { module: ts.ModuleKind.CommonJS, esModuleInterop: true },
    }).outputText;
    const module = new Module(path);
    module.paths = require.resolve.paths("react");
    module._compile(compiled, path);
    return module.exports;
}

const { emptyPanel, gateMessage } = loadTypescript("src/panel-state.ts");
assert.equal(emptyPanel.gate, "None");
assert.equal(gateMessage("Green"), "Passing window open");
assert.match(gateMessage("Yellow"), /no new passes/);
assert.match(gateMessage("Red"), /No new passing entry/);
assert.equal(gateMessage("None"), "No passing pool");

const backend = fs.readFileSync("../src/Presentation/VehicleControlsUISystem.cs", "utf8");
const frontend = fs.readFileSync("src/index.tsx", "utf8");
const properties = [...backend.matchAll(/writer.PropertyName\("([^"]+)"\)/g)].map(match => match[1]).sort();
assert.deepEqual(properties, Object.keys(emptyPanel).sort(), "Serialized snapshot and typed UI fallback must match");
const bindings = source => [...source.matchAll(/(?:new ValueBinding<[^>]+>\(Domain|bindValue\(domain), "([^"]+)"/g)].map(match => match[1]).sort();
assert.deepEqual(bindings(backend), bindings(frontend), "Every UI binding must have a backend publisher");
const uniqueTriggers = source => [...new Set([...source.matchAll(/(?:new TriggerBinding(?:<[^>]+>)?\(Domain|trigger\(domain), "([^"]+)"/g)].map(match => match[1]))].sort();
for (const command of uniqueTriggers(frontend)) assert.ok(uniqueTriggers(backend).includes(command), "Button command must have a backend trigger: " + command);
assert.match(frontend, /trigger\(domain, "togglePassing"\)/);
assert.doesNotMatch(frontend, /passing-toggle-buttons|trigger\(domain, "setPassingEnabled"/,
    "The master control must be one stateful native button");
assert.equal(gateMessage("Purple"), "Road unsuitable for passing");
assert.match(frontend, /state\.isLeader && pooled/);
assert.match(backend, /state\.Completed = pool\.CompletedPasses/);
assert.match(backend, /pools\.TryTraversal\(entity, out var lease\)[\s\S]*else if \(leased\)/, "A detached lease still shows its recovery state");
assert.match(backend, /traversal.State == TraversalState.Committed \|\| traversal.State == TraversalState.Passing\) state.Active\+\+/,
    "Returning traversals must not inflate the passing count");
assert.doesNotMatch(frontend, /poolDetails|split\("\\n"\)/, "The panel must not parse a text dump");
assert.match(backend, /traversal.State != TraversalState.Passing \|\| traversal.ReturnBlock == NativeReturnBlock.None\) continue/,
    "Ordinary committed/returning vehicles must not be reported as waiting to return");

const { containsTab, addTab, preserveTab } = loadTypescript("src/header-tree.ts");
let closed = 0, selected = "";
const tab = React.createElement("native-tab", { selectedId: "info", onSelect: id => { selected = id; } });
const tooltip = React.createElement("native-tooltip", null, tab);
const header = React.createElement("header", null, React.createElement("div", { className: "native-tabs" }, tooltip));
assert.ok(containsTab(header, "native-tabs"));
assert.equal(containsTab(header, "missing"), false);
const control = React.createElement("native-tab", { id: "passing-controls" });
const changed = addTab(header, "native-tabs", control, () => closed++, true);
const children = React.Children.toArray(changed.props.children.props.children);
assert.equal(children.length, 2);
assert.equal(children[1].props.id, "passing-controls");
assert.equal(Array.isArray(children[0].props.children), false, "Native Tooltip needs a single element child");
const preserved = children[0].props.children;
preserved.props.onSelect("info-next");
assert.equal(closed, 1);
assert.equal(selected, "info-next");
assert.equal(preserved.props.selectedId, "passing-controls-active");
assert.equal(tab.props.selectedId, "info", "Header augmentation must not mutate vanilla elements");
assert.equal(preserveTab("text", () => {}, false), "text");
assert.equal(addTab(header, "missing", control, () => {}, false).props.children.props.children.props.children, tab);
console.log("UI snapshot, command, gate and native header contracts passed.");
