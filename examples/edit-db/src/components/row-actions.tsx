import { useRef, useEffect } from 'react';
import { Pencil, Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/button';

interface RowActionsProps {
    anchorEl: HTMLElement;
    onEdit?: () => void;
    onDelete?: () => void;
    onMouseEnter: () => void;
    onDismiss: () => void;
}

/**
 * Floating row action toolbar using the Popover API.
 * Overlays the row it's anchored to — pinned to the row's right edge and
 * vertically centered — so the buttons sit ON the row and are easy to reach,
 * rather than floating below it.
 */
export function RowActions({ anchorEl, onEdit, onDelete, onMouseEnter, onDismiss }: RowActionsProps) {
    const ref = useRef<HTMLDivElement>(null);

    useEffect(() => {
        const el = ref.current;
        if (!el) return;
        const update = () => {
            const rowRect = anchorEl.getBoundingClientRect();
            const popH = el.offsetHeight;
            const popW = el.offsetWidth;
            // Center vertically within the row; pin to the right edge, clamped to
            // the viewport so a horizontally-scrolled row keeps the toolbar visible.
            el.style.top = `${rowRect.top + (rowRect.height - popH) / 2}px`;
            const left = Math.min(rowRect.right - popW - 8, window.innerWidth - popW - 8);
            el.style.left = `${Math.max(8, left)}px`;
        };
        update();
        el.showPopover();
        return () => { try { el.hidePopover(); } catch { /* already hidden */ } };
    }, [anchorEl]);

    // Dismiss on a tap/click outside the toolbar — the touch equivalent of
    // mouse-leave, since a held-open overlay has no pointer to leave.
    useEffect(() => {
        const onDocPointerDown = (e: PointerEvent) => {
            const el = ref.current;
            if (el && !el.contains(e.target as Node)) onDismiss();
        };
        document.addEventListener('pointerdown', onDocPointerDown);
        return () => document.removeEventListener('pointerdown', onDocPointerDown);
    }, [onDismiss]);

    return (
        <div
            ref={ref}
            // @ts-expect-error popover attribute not yet in React types
            popover="manual"
            className="flex items-center gap-0.5 rounded-md border border-border bg-popover px-1 py-0.5 shadow-md m-0"
            style={{ position: 'fixed' }}
            onClick={(e) => e.stopPropagation()}
            onMouseEnter={onMouseEnter}
            onMouseLeave={onDismiss}
        >
            {onEdit && (
                <Button
                    variant="ghost"
                    size="icon-sm"
                    onClick={onEdit}
                    aria-label="Edit row"
                    title="Edit"
                    className="size-6"
                >
                    <Pencil className="size-3.5" />
                </Button>
            )}
            {onDelete && (
                <Button
                    variant="ghost"
                    size="icon-sm"
                    onClick={onDelete}
                    aria-label="Delete row"
                    title="Delete"
                    className="size-6 text-destructive hover:text-destructive"
                >
                    <Trash2 className="size-3.5" />
                </Button>
            )}
        </div>
    );
}
