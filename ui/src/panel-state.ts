export type PoolGate = "Green" | "Yellow" | "Red" | "Purple";
export interface PanelState {
    gate: "None" | PoolGate;
    role: string;
    currentSpeed: string;
    leaderSpeed: string;
    queued: number;
    active: number;
    returning: number;
    waitingReturn: number;
    reason: string;
    entryReason: string;
    returnReason: string;
    corridor: string;
    yellowBoundary: string;
    redBoundary: string;
    availableDistance: string;
    coverage: string;
    oncoming: string;
    isLeader: boolean;
    completed: number;
    poolStatus: string;
    modCost: string;
}

export const emptyPanel: PanelState = {
    gate: "None", role: "No passing pool", currentSpeed: "Unavailable", leaderSpeed: "",
    queued: 0, active: 0, returning: 0, waitingReturn: 0, reason: "", entryReason: "", returnReason: "",
    corridor: "", yellowBoundary: "", redBoundary: "", availableDistance: "",
    coverage: "", oncoming: "", isLeader: false, completed: 0, poolStatus: "No passing queue", modCost: "",
};

export function gateMessage(gate: PanelState["gate"]): string {
    switch (gate) {
        case "None": return "No passing pool";
        case "Green": return "Passing window open";
        case "Yellow": return "Window closing; no new passes";
        case "Red": return "No new passing entry";
        case "Purple": return "Road unsuitable for passing";
        default: {
            const exhaustive: never = gate;
            return exhaustive;
        }
    }
}
