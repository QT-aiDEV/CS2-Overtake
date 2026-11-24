import React, { ReactNode, useEffect, useState } from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import { ModRegistrar } from "cs2/modding";
import { FocusDisabled } from "cs2/input";
import { Button, PanelSection, PanelSectionRow, Tooltip } from "cs2/ui";
import { addTab, containsTab } from "./header-tree";
import { emptyPanel, gateMessage } from "./panel-state";
import "./style.css";

const domain = "Overtake";
const nameBinding = bindValue(domain, "selectedName", "");
const roadVehicleBinding = bindValue(domain, "roadVehicle", false);
const debuggerBinding = bindValue(domain, "debugOverlayEnabled", false);
const passingBinding = bindValue(domain, "passingEnabled", false);
const panelBinding = bindValue(domain, "panelState", emptyPanel);
export const hasCSS = true;
let actionTheme: { button: string } | undefined;

function PassingControls() {
    const debug = useValue(debuggerBinding);
    const passing = useValue(passingBinding);
    const state = useValue(panelBinding);
    const [detailsOpen, setDetailsOpen] = useState(false);
    const pooled = state.gate !== "None";
    const recovering = !pooled && state.role !== "No passing pool";
    return <FocusDisabled disabled><PanelSection>
        <div className="passing-controls-content">
            <div className="passing-status">
                {pooled && <div className="passing-gate-header">
                    {/* Read-only lamps show the canonical gate. Purple is structural road invalidity. */}
                    <div className={`passing-lights passing-lights-${state.gate.toLowerCase()}`} aria-hidden="true">
                        <span className="passing-lamp passing-lamp-red" />
                        <span className="passing-lamp passing-lamp-yellow" />
                        <span className="passing-lamp passing-lamp-green" />
                        <span className="passing-lamp passing-lamp-purple" />
                    </div>
                    <strong className={`passing-gate-name passing-gate-${state.gate.toLowerCase()}`}>
                        {state.gate.toUpperCase()}
                    </strong>
                </div>}
                <p className="passing-status-title">{pooled ? gateMessage(state.gate) : recovering ? "Returning to normal traffic" : "No passing queue"}</p>
                {pooled && <p className="passing-note passing-reason">{state.reason}</p>}
                <PanelSectionRow left="This vehicle" right={state.role} />
                <PanelSectionRow left="Current speed" right={state.currentSpeed} />
                {state.entryReason && <PanelSectionRow left="This vehicle's entry" right={state.entryReason} />}
                {state.isLeader && pooled ? <>
                    <div className="passing-counts">
                        <div><strong>{state.queued}</strong><span>Queued</span></div>
                        <div><strong>{state.active}</strong><span>Passing</span></div>
                        <div><strong>{state.returning}</strong><span>Returning</span></div>
                    </div>
                    <PanelSectionRow left="Completed passes" right={state.completed} />
                    <p className="passing-note">Successfully cleared this slow vehicle during this queue.</p>
                </> : pooled && <PanelSectionRow left="Slow leader ahead" right={state.leaderSpeed} />}
                {pooled && <PanelSectionRow left="Queue status" right={state.poolStatus} />}
                {recovering && <p className="passing-note">The queue has ended. This vehicle is attempting to return to normal traffic.</p>}
            </div>
            <div className="passing-actions">
                <PanelSectionRow left="Automatic passing" right={
                    <Button theme={passing ? actionTheme : undefined} className="passing-master" selected={passing} aria-pressed={passing}
                        aria-label={`Automatic passing ${passing ? "on" : "off"}`}
                        onSelect={() => trigger(domain, "togglePassing")}
                        tooltipLabel="Off stops new passes; active passes return safely.">
                        {passing ? "ON" : "OFF"}
                    </Button>
                } />
                {!passing && <p className="passing-note">Off stops new passes; active passes return safely.</p>}
                <PanelSectionRow left="Road radar" right={
                    <Button className="passing-secondary passing-radar" selected={debug} aria-pressed={debug}
                        aria-label={`Road radar ${debug ? "on" : "off"}`}
                        onSelect={() => trigger(domain, "toggleDebugOverlay")}
                        tooltipLabel="Show the shared road radar and nearest oncoming traffic.">
                        {debug ? "ON" : "OFF"}
                    </Button>
                } />
                {state.modCost && <Tooltip tooltip="Average main-thread time the mod adds per simulation tick.">
                    <PanelSectionRow left="Mod cost" right={state.modCost} />
                </Tooltip>}
            </div>
            {pooled && <div className="passing-details">
                <Button className="passing-secondary passing-details-toggle" selected={detailsOpen} aria-expanded={detailsOpen}
                    onSelect={() => setDetailsOpen(!detailsOpen)}>
                    {detailsOpen ? "Hide road and traffic details" : "Show road and traffic details"}
                </Button>
                {detailsOpen && <>
                    {!state.isLeader && <>
                        <PanelSectionRow left="Queued" right={state.queued} />
                        <PanelSectionRow left="Passing" right={state.active} />
                        <PanelSectionRow left="Returning" right={state.returning} />
                    </>}
                    <PanelSectionRow left="Gate reason" right={state.reason} />
                    <PanelSectionRow left="Verified road ahead" right={state.corridor} />
                    <PanelSectionRow left="Available distance" right={state.availableDistance} />
                    <PanelSectionRow left="Yellow boundary ahead" right={state.yellowBoundary} />
                    <PanelSectionRow left="Red boundary ahead" right={state.redBoundary} />
                    <PanelSectionRow left="Traffic coverage" right={state.coverage} />
                    <PanelSectionRow left="Nearest oncoming" right={state.oncoming} />
                    {state.waitingReturn > 0 && <PanelSectionRow left="First return wait" right={state.returnReason} />}
                    {debug && <p className="passing-legend">Green allows new passes. Yellow and red close entry. Purple marks unsuitable road.</p>}
                </>}
            </div>}
        </div>
    </PanelSection></FocusDisabled>;
}
class VehicleControlsBoundary extends React.Component<{ children: ReactNode; fallback: ReactNode }, { failed: boolean }> {
    state = { failed: false };
    static getDerivedStateFromError() { return { failed: true }; }
    componentDidCatch(error: Error, info: React.ErrorInfo) {
        console.error(`Overtake.UI.renderFailed: ${error.stack || error.message}\n${info.componentStack || ""}`);
    }
    render() { return this.state.failed ? this.props.fallback : this.props.children; }
}

const registerControls: ModRegistrar = registry => {
    if (!Button || !PanelSection || !PanelSectionRow || !Tooltip) return;
    const modulePath = "game-ui/common/panel/panel.tsx";
    const themePath = "game-ui/game/components/selected-info-panel/selected-info-panel.module.scss";
    // The installed panel's inner component is private. Extend its exported Panel boundary,
    // preserve the vanilla element tree, and act only on the selected-info theme.
    if (!registry.registry.has(modulePath) || !registry.registry.has(themePath)) return;
    const theme: unknown = registry.get(themePath, "classes");
    if (typeof theme !== "object" || theme === null || !("selectedInfoPanel" in theme)
        || !("tabBar" in theme) || !("tabIcon" in theme) || typeof theme.tabIcon !== "string" || typeof theme.selectedInfoPanel !== "string" || typeof theme.tabBar !== "string") return;
    const selectedClass = theme.selectedInfoPanel;
    const tabsPath = "game-ui/common/tabs/tabs.tsx";
    const primaryPath = "game-ui/common/input/button/themes/primary-button.module.scss";
    if (!registry.registry.has(tabsPath) || !registry.registry.has(primaryPath)) return;
    const nativeTab: unknown = registry.get(tabsPath, "Tab");
    const primary: unknown = registry.get(primaryPath, "classes");
    if (typeof nativeTab !== "function" || typeof primary !== "object" || primary === null
        || !("button" in primary) || typeof primary.button !== "string") return;
    actionTheme = { button: primary.button };
    const Tab = nativeTab as React.ComponentType<{
        id: string; selectedId: string | null; onSelect: (id: string) => void; children: ReactNode;
    }>;
    const tabClass = theme.tabBar;
    // Preserve the original panel ref through the additive native tab shell.
    registry.extend(modulePath, "Panel", Original => {
        type PanelInput = { className?: string; header?: ReactNode; children?: ReactNode };
        const WithControls = React.forwardRef<HTMLDivElement, PanelInput>((props, ref) => {
            const name = useValue(nameBinding);
            const roadVehicle = useValue(roadVehicleBinding);
            const [open, setOpen] = useState(false);
            useEffect(() => setOpen(false), [name]);
            if (!props.className?.split(" ").includes(selectedClass) || !roadVehicle)
                return <Original {...props} ref={ref} />;
            // Match the selected-info panel's own Tooltip > Tab structure.
            const button = <FocusDisabled key="passing-controls-tab" disabled><Tooltip tooltip="Vehicle controls"><Tab
                id="passing-controls" selectedId={open ? "passing-controls" : null} onSelect={() => setOpen(true)}>
                <svg className={`${theme.tabIcon} passing-tab-glyph`} viewBox="0 0 24 24" aria-hidden="true">
                    <path fillRule="evenodd" d="M6.6 4.5A2.2 2.2 0 0 1 8.7 3h6.6a2.2 2.2 0 0 1 2.1 1.5L19 9h.5A2.5 2.5 0 0 1 22 11.5V17a2 2 0 0 1-2 2v2h-2.5v-2h-11v2H4v-2a2 2 0 0 1-2-2v-5.5A2.5 2.5 0 0 1 4.5 9H5l1.6-4.5ZM8.7 5.5 7.4 9h9.2l-1.3-3.5H8.7ZM5.7 13a1.5 1.5 0 1 0 0 3 1.5 1.5 0 0 0 0-3Zm12.6 0a1.5 1.5 0 1 0 0 3 1.5 1.5 0 0 0 0-3Z" />
                </svg>
            </Tab></Tooltip></FocusDisabled>;
            const header = containsTab(props.header, tabClass)
                ? addTab(props.header, tabClass, button, () => setOpen(false), open)
                : <>{props.header}<div className="passing-controls-row">{button}</div></>;
            return <Original {...props} ref={ref} header={header}>
                {open ? <PassingControls /> : props.children}
            </Original>;
        });
        return React.forwardRef<HTMLDivElement, PanelInput>((props, ref) =>
            <VehicleControlsBoundary fallback={<Original {...props} ref={ref} />}>
                <WithControls {...props} ref={ref} />
            </VehicleControlsBoundary>);
    });
    console.info(`Overtake.UI.registered build=0.1.7 hasCSS=${hasCSS}`);
};
const register: ModRegistrar = registry => {
    try { registerControls(registry); }
    catch (error: unknown) {
        console.error("Overtake.UI.registrationFailed", error);
    }
};
export default register;
