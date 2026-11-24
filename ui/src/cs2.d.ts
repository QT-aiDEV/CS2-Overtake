// Narrow contracts for the installed CS2 UI surfaces used by this module.
declare module "cs2/api" {
    export interface ValueBinding<T> { readonly value: T }
    export function bindValue<T>(group: string, name: string, fallback: T): ValueBinding<T>;
    export function useValue<T>(binding: ValueBinding<T>): T;
    export function trigger(group: string, name: string, ...args: unknown[]): void;
}
declare module "cs2/modding" {
    import type { ComponentType, ForwardRefExoticComponent, PropsWithoutRef, ReactNode, RefAttributes } from "react";
    export interface PanelInput { className?: string; header?: ReactNode; children?: ReactNode }
    type PanelComponent = ComponentType<PanelInput & RefAttributes<HTMLDivElement>>;
    type PanelExtension = ForwardRefExoticComponent<PropsWithoutRef<PanelInput> & RefAttributes<HTMLDivElement>>;
    export interface ModuleRegistry {
        registry: Map<string, unknown>;
        get(path: string, name: string): unknown;
        extend(path: string, name: string, callback: (original: PanelComponent) => PanelExtension): void;
    }
    export type ModRegistrar = (registry: ModuleRegistry) => void;
}
declare module "cs2/input" {
    import type { PropsWithChildren } from "react";
    export function FocusDisabled(props: PropsWithChildren<{ disabled: boolean }>): JSX.Element;
}
declare module "cs2/ui" {
    import type { ButtonHTMLAttributes, PropsWithChildren, ReactNode } from "react";
    export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
        selected?: boolean;
        theme?: { button?: string };
        tooltipLabel?: ReactNode;
        onSelect?: () => void;
    }
    export function Button(props: ButtonProps): JSX.Element;
    export function PanelSection(props: PropsWithChildren): JSX.Element;
    export function PanelSectionRow(props: { left: ReactNode; right: ReactNode }): JSX.Element;
    export function Tooltip(props: PropsWithChildren<{ tooltip: ReactNode }>): JSX.Element;
}
