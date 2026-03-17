import { createContext, useContext, useCallback, useState, ReactNode } from 'react';
import type { ColumnPanel } from '../data-panel';

interface ColumnNavEntry {
    panel: ColumnPanel;
    element: HTMLElement | null;
}

interface ColumnNavState {
    mainTable: string | null;
    columns: ColumnNavEntry[];
    focusedIndex: number;
    focusColumn: (index: number) => void;
    closeColumn: (index: number) => void;
}

const defaultState: ColumnNavState = {
    mainTable: null,
    columns: [],
    focusedIndex: 0,
    focusColumn: () => {},
    closeColumn: () => {},
};

const ColumnNavContext = createContext<ColumnNavState>(defaultState);

interface ColumnNavRegistration {
    mainTable: string | null;
    columns: ColumnPanel[];
    columnRefs: Map<number, HTMLElement | null>;
    onClose: (index: number) => void;
}

const RegistrationContext = createContext<{
    register: (reg: ColumnNavRegistration) => void;
    unregister: () => void;
}>({ register: () => {}, unregister: () => {} });

export function ColumnNavProvider({ children }: { children: ReactNode }) {
    const [registration, setRegistration] = useState<ColumnNavRegistration | null>(null);
    const [focusedIndex, setFocusedIndex] = useState(0);

    const register = useCallback((reg: ColumnNavRegistration) => {
        setRegistration(reg);
    }, []);

    const unregister = useCallback(() => {
        setRegistration(null);
        setFocusedIndex(0);
    }, []);

    const focusColumn = useCallback((index: number) => {
        setFocusedIndex(index);
        if (!registration) return;
        // index 0 = main panel, index 1+ = side columns (stored at index-1 in columnRefs)
        const el = registration.columnRefs.get(index);
        if (el) {
            el.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'nearest' });
        }
    }, [registration]);

    const closeColumn = useCallback((index: number) => {
        if (!registration) return;
        // index 0 is main panel (cannot close), index 1+ maps to openColumns[index-1]
        if (index > 0) {
            registration.onClose(index - 1);
        }
    }, [registration]);

    const columns: ColumnNavEntry[] = registration
        ? registration.columns.map((panel, i) => ({
            panel,
            element: registration.columnRefs.get(i + 1) ?? null,
        }))
        : [];

    const navState: ColumnNavState = {
        mainTable: registration?.mainTable ?? null,
        columns,
        focusedIndex,
        focusColumn,
        closeColumn,
    };

    return (
        <RegistrationContext.Provider value={{ register, unregister }}>
            <ColumnNavContext.Provider value={navState}>
                {children}
            </ColumnNavContext.Provider>
        </RegistrationContext.Provider>
    );
}

export function useColumnNav() {
    return useContext(ColumnNavContext);
}

export function useColumnNavRegister() {
    return useContext(RegistrationContext);
}
