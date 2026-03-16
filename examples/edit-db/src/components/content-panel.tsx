import { useState, useCallback, useEffect, useRef } from 'react';
import { detectContentKind, type ContentKind } from '@/lib/content-detect';
import { cn } from '@/lib/utils';
import { Button } from '@/components/ui/button';
import {
    Sheet,
    SheetContent,
    SheetHeader,
    SheetTitle,
    SheetDescription,
} from '@/components/ui/sheet';
import {
    Dialog,
    DialogContent,
    DialogHeader,
    DialogTitle,
    DialogDescription,
} from '@/components/ui/dialog';
import {
    Braces,
    Code,
    Database,
    FileText,
    Binary,
    Maximize2,
    Minimize2,
    Copy,
    Check,
    Pencil,
    ChevronUp,
    ChevronDown,
    WrapText,
} from 'lucide-react';

const kindIcons: Record<ContentKind, typeof Braces> = {
    json: Braces,
    xml: Code,
    'php-serialized': Database,
    binary: Binary,
    longtext: FileText,
    text: FileText,
};

const kindLabels: Record<ContentKind, string> = {
    json: 'JSON',
    xml: 'XML',
    'php-serialized': 'PHP Serialized',
    binary: 'Binary',
    longtext: 'Text',
    text: 'Text',
};

function formatContent(value: string, kind: ContentKind): string {
    if (kind === 'json') {
        try {
            return JSON.stringify(JSON.parse(value), null, 2);
        } catch {
            return value;
        }
    }
    if (kind === 'xml') {
        return value.replace(/>\s*</g, '>\n<').trim();
    }
    return value;
}

export interface ContentPanelTarget {
    value: string;
    columnName: string;
    columnLabel: string;
    dbType: string;
    rowIndex: number;
    isReadOnly: boolean;
}

interface ContentPanelProps {
    target: ContentPanelTarget | null;
    onClose: () => void;
    onNavigate: (direction: 'prev' | 'next') => void;
    onSave?: (value: string) => void;
    canNavigatePrev: boolean;
    canNavigateNext: boolean;
}

export function ContentPanel({
    target,
    onClose,
    onNavigate,
    onSave,
    canNavigatePrev,
    canNavigateNext,
}: ContentPanelProps) {
    const [fullScreen, setFullScreen] = useState(false);
    const [editing, setEditing] = useState(false);
    const [editValue, setEditValue] = useState('');
    const [copied, setCopied] = useState(false);
    const textareaRef = useRef<HTMLTextAreaElement>(null);

    const value = target?.value ?? '';
    const kind = detectContentKind(value, target?.dbType);
    const Icon = kindIcons[kind];
    const formatted = formatContent(value, kind);

    // Reset edit state when target changes
    useEffect(() => {
        setEditing(false);
        setEditValue('');
        setCopied(false);
    }, [target?.rowIndex, target?.columnName]);

    // Keyboard navigation
    useEffect(() => {
        if (!target) return;
        const handler = (e: KeyboardEvent) => {
            if (editing) return;
            if (e.key === 'ArrowUp' && canNavigatePrev) {
                e.preventDefault();
                onNavigate('prev');
            } else if (e.key === 'ArrowDown' && canNavigateNext) {
                e.preventDefault();
                onNavigate('next');
            }
        };
        window.addEventListener('keydown', handler);
        return () => window.removeEventListener('keydown', handler);
    }, [target, editing, canNavigatePrev, canNavigateNext, onNavigate]);

    const handleCopy = useCallback(() => {
        navigator.clipboard.writeText(value);
        setCopied(true);
        setTimeout(() => setCopied(false), 1500);
    }, [value]);

    const handleStartEdit = useCallback(() => {
        setEditValue(formatted);
        setEditing(true);
        requestAnimationFrame(() => textareaRef.current?.focus());
    }, [formatted]);

    const handleCancelEdit = useCallback(() => {
        setEditing(false);
        setEditValue('');
    }, []);

    const handleSaveEdit = useCallback(() => {
        if (onSave && editValue !== value) {
            onSave(editValue);
        }
        setEditing(false);
    }, [onSave, editValue, value]);

    const handleFormat = useCallback(() => {
        if (kind === 'json') {
            try {
                setEditValue(JSON.stringify(JSON.parse(editValue), null, 2));
            } catch { /* leave as-is */ }
        } else if (kind === 'xml') {
            setEditValue(editValue.replace(/>\s*</g, '>\n<').trim());
        }
    }, [editValue, kind]);

    const canFormat = kind === 'json' || kind === 'xml';
    const canEdit = !target?.isReadOnly && kind !== 'binary';

    const headerContent = (
        <div className="flex items-center gap-2 min-w-0">
            <Icon className="size-4 text-muted-foreground shrink-0" />
            <span className="truncate font-semibold text-sm">{target?.columnLabel}</span>
            <span className="text-xs text-muted-foreground shrink-0">{kindLabels[kind]}</span>
            <span className="text-xs text-muted-foreground shrink-0">{value.length} chars</span>
        </div>
    );

    const toolbarContent = (
        <div className="flex items-center gap-1 px-4 pb-2">
            <Button
                type="button"
                variant="ghost"
                size="sm"
                className="h-7 text-xs px-2"
                onClick={handleCopy}
            >
                {copied ? <Check className="size-3" /> : <Copy className="size-3" />}
                {copied ? 'Copied' : 'Copy'}
            </Button>
            {canEdit && !editing && (
                <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    className="h-7 text-xs px-2"
                    onClick={handleStartEdit}
                >
                    <Pencil className="size-3" />
                    Edit
                </Button>
            )}
            {editing && canFormat && (
                <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    className="h-7 text-xs px-2"
                    onClick={handleFormat}
                >
                    <WrapText className="size-3" />
                    Format
                </Button>
            )}
            <div className="flex items-center gap-1 ml-auto">
                <Button
                    type="button"
                    variant="ghost"
                    size="icon-sm"
                    onClick={() => onNavigate('prev')}
                    disabled={!canNavigatePrev}
                    aria-label="Previous row"
                    title="Previous row"
                >
                    <ChevronUp className="size-4" />
                </Button>
                <Button
                    type="button"
                    variant="ghost"
                    size="icon-sm"
                    onClick={() => onNavigate('next')}
                    disabled={!canNavigateNext}
                    aria-label="Next row"
                    title="Next row"
                >
                    <ChevronDown className="size-4" />
                </Button>
                <Button
                    type="button"
                    variant="ghost"
                    size="icon-sm"
                    onClick={() => setFullScreen((f) => !f)}
                    aria-label={fullScreen ? 'Exit full screen' : 'Full screen'}
                    title={fullScreen ? 'Exit full screen' : 'Full screen'}
                >
                    {fullScreen ? <Minimize2 className="size-4" /> : <Maximize2 className="size-4" />}
                </Button>
            </div>
        </div>
    );

    const bodyContent = (
        <div className="flex-1 min-h-0 overflow-auto px-4 pb-4">
            {editing ? (
                <div className="flex flex-col gap-2 h-full">
                    <textarea
                        ref={textareaRef}
                        value={editValue}
                        onChange={(e) => setEditValue(e.target.value)}
                        className={cn(
                            'flex-1 w-full rounded-md border border-input bg-background px-3 py-2',
                            'text-xs font-mono leading-relaxed',
                            'ring-offset-background focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2',
                            'resize-none min-h-[200px]',
                        )}
                    />
                    <div className="flex items-center gap-2 justify-end">
                        <Button type="button" variant="ghost" size="sm" onClick={handleCancelEdit}>
                            Cancel
                        </Button>
                        <Button type="button" size="sm" onClick={handleSaveEdit}>
                            Save
                        </Button>
                    </div>
                </div>
            ) : (
                <pre className="text-xs font-mono leading-relaxed whitespace-pre-wrap break-all">
                    {formatted}
                </pre>
            )}
        </div>
    );

    const isOpen = !!target;

    return (
        <>
            <Sheet open={isOpen && !fullScreen} onOpenChange={(open) => { if (!open) onClose(); }}>
                <SheetContent side="right" className="w-[480px] max-w-[90vw]">
                    <SheetHeader>
                        <SheetTitle>{headerContent}</SheetTitle>
                        <SheetDescription className="sr-only">Content viewer panel</SheetDescription>
                    </SheetHeader>
                    {toolbarContent}
                    {bodyContent}
                </SheetContent>
            </Sheet>
            <Dialog open={isOpen && fullScreen} onOpenChange={(open) => { if (!open) setFullScreen(false); }}>
                <DialogContent className="max-w-[90vw] max-h-[90vh] w-full h-full flex flex-col">
                    <DialogHeader>
                        <DialogTitle>{headerContent}</DialogTitle>
                        <DialogDescription className="sr-only">Content viewer full screen</DialogDescription>
                    </DialogHeader>
                    {toolbarContent}
                    {bodyContent}
                </DialogContent>
            </Dialog>
        </>
    );
}
