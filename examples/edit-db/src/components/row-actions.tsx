import { useRef, useEffect } from 'react';
import { Pencil, Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/button';

interface RowActionsProps {
    anchorEl: HTMLElement;
    onEdit: () => void;
    onDelete?: () => void;
    onMouseEnter: () => void;
    onDismiss: () => void;
}

/**
 * Floating row action toolbar using the Popover API.
 * Anchored to the row element, positioned below-left with automatic
 * flip to above when near the viewport bottom.
 */
export function RowActions({ anchorEl, onEdit, onDelete, onMouseEnter, onDismiss }: RowActionsProps) {
    const ref = useRef<HTMLDivElement>(null);

    useEffect(() => {
        const el = ref.current;
        if (!el) return;
        const update = () => {
            const rowRect = anchorEl.getBoundingClientRect();
            const popH = el.offsetHeight;
            const spaceBelow = window.innerHeight - rowRect.bottom;
            const above = spaceBelow < popH + 4;
            el.style.top = above
                ? `${rowRect.top - popH - 2}px`
                : `${rowRect.bottom + 2}px`;
            el.style.left = `${rowRect.left + 4}px`;
        };
        update();
        el.showPopover();
        return () => { try { el.hidePopover(); } catch { /* already hidden */ } };
    }, [anchorEl]);

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
