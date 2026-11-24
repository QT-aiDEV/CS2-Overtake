import React, { ReactNode } from "react";

type HeaderProps = { children?: ReactNode; className?: string; onSelect?: unknown; selectedId?: unknown };

// Native Tooltip/DOMNodeModifier require one element, not a one-element array.
function mapShape(node: ReactNode, transform: (child: ReactNode) => ReactNode): ReactNode {
    return Array.isArray(node) ? node.map(child => mapShape(child, transform)) : transform(node);
}

export function preserveTab(node: ReactNode, closeControls: () => void, controlsOpen: boolean): ReactNode {
    if (!React.isValidElement<HeaderProps>(node)) return node;
    const onSelect = node.props.onSelect;
    const overrides = typeof onSelect === "function"
        ? { onSelect: (...args: unknown[]) => { closeControls(); onSelect(...args); },
            ...(controlsOpen && "selectedId" in node.props ? { selectedId: "passing-controls-active" } : {}) }
        : {};
    if (node.props.children === undefined)
        return typeof onSelect === "function" ? React.cloneElement(node, overrides) : node;
    return React.cloneElement(node, overrides,
        mapShape(node.props.children, child => preserveTab(child, closeControls, controlsOpen)));
}

export function addTab(node: ReactNode, tabClass: string, button: ReactNode, closeControls: () => void, controlsOpen: boolean): ReactNode {
    if (!React.isValidElement<HeaderProps>(node)) return node;
    const classes = typeof node.props.className === "string" ? node.props.className.split(" ") : [];
    if (classes.includes(tabClass)) {
        return React.cloneElement(node, undefined,
            mapShape(node.props.children, child => preserveTab(child, closeControls, controlsOpen)), button);
    }
    if (node.props.children === undefined) return node;
    return React.cloneElement(node, undefined,
        mapShape(node.props.children, child => addTab(child, tabClass, button, closeControls, controlsOpen)));
}

export function containsTab(node: ReactNode, tabClass: string): boolean {
    if (!React.isValidElement<HeaderProps>(node)) return false;
    if (typeof node.props.className === "string" && node.props.className.split(" ").includes(tabClass)) return true;
    return React.Children.toArray(node.props.children).some(child => containsTab(child, tabClass));
}
