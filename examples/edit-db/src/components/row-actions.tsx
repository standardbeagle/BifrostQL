import { useRef, useState, useLayoutEffect } from 'react';
import { Pencil, Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/button';

interface RowActionsProps {
    onEdit: () => void;
    onDelete?: () => void;
}

/**
 * Floating row action toolbar. Renders as an absolutely positioned bar
 * that appears on row hover, below the row by default, flipping above
 * when there isn't enough room below.
 *
 * Must be placed inside a `position: relative` container (the TableRow wrapper).
 */
export function RowActions({ onEdit, onDelete }: RowActionsProps) {
    const ref = useRef<HTMLDivElement>(null);
    const [above, setAbove] = useState(false);

    useLayoutEffect(() => {
        const el = ref.current;
        if (!el) return;
        const rect = el.getBoundingClientRect();
        // Flip above if the toolbar would go off the bottom of the viewport
        setAbove(rect.bottom > window.innerHeight);
    });

    return (
        <div
            ref={ref}
            className={`
                absolute left-1 z-20 flex items-center gap-0.5
                rounded-md border border-border bg-popover px-1 py-0.5
                shadow-md
                ${above ? 'bottom-full mb-0.5' : 'top-full mt-0.5'}
            `}
            onClick={(e) => e.stopPropagation()}
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
