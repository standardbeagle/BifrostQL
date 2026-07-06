import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { CheckCircle2, AlertCircle, X } from 'lucide-react';
import { cn } from '@/lib/utils';

/**
 * Minimal dependency-free toast system for transient success/error feedback
 * after mutations (create, save, delete, inline content edits). Kept small and
 * self-contained so the published editor pulls in no extra runtime dependency.
 */

export type ToastVariant = 'success' | 'error';

interface Toast {
    id: number;
    message: string;
    variant: ToastVariant;
}

interface ToastApi {
    toast: (message: string, variant?: ToastVariant) => void;
}

const ToastContext = createContext<ToastApi>({ toast: () => {} });

const AUTO_DISMISS_MS = 4000;

export function ToastProvider({ children }: { children: ReactNode }) {
    const [toasts, setToasts] = useState<Toast[]>([]);
    const nextId = useRef(0);
    const timers = useRef<Set<ReturnType<typeof setTimeout>>>(new Set());

    const dismiss = useCallback((id: number) => {
        setToasts((prev) => prev.filter((t) => t.id !== id));
    }, []);

    const toast = useCallback((message: string, variant: ToastVariant = 'success') => {
        const id = nextId.current++;
        setToasts((prev) => [...prev, { id, message, variant }]);
        const handle = setTimeout(() => {
            dismiss(id);
            timers.current.delete(handle);
        }, AUTO_DISMISS_MS);
        timers.current.add(handle);
    }, [dismiss]);

    // Clear any pending auto-dismiss timers on unmount.
    useEffect(() => {
        const pending = timers.current;
        return () => { pending.forEach(clearTimeout); };
    }, []);

    const api = useMemo(() => ({ toast }), [toast]);

    return (
        <ToastContext.Provider value={api}>
            {children}
            <div
                className="fixed bottom-4 right-4 z-[100] flex flex-col gap-2 max-w-sm"
                role="region"
                aria-label="Notifications"
            >
                {toasts.map((t) => (
                    <div
                        key={t.id}
                        role="status"
                        className={cn(
                            'flex items-start gap-2 rounded-md border px-3 py-2 text-sm shadow-md bg-popover',
                            t.variant === 'error'
                                ? 'border-destructive/40 text-destructive'
                                : 'border-border text-foreground',
                        )}
                    >
                        {t.variant === 'error'
                            ? <AlertCircle className="size-4 shrink-0 mt-0.5" />
                            : <CheckCircle2 className="size-4 shrink-0 mt-0.5 text-green-600" />}
                        <span className="flex-1 break-words">{t.message}</span>
                        <button
                            type="button"
                            onClick={() => dismiss(t.id)}
                            aria-label="Dismiss notification"
                            className="shrink-0 opacity-60 hover:opacity-100"
                        >
                            <X className="size-3.5" />
                        </button>
                    </div>
                ))}
            </div>
        </ToastContext.Provider>
    );
}

export function useToast(): ToastApi {
    return useContext(ToastContext);
}
