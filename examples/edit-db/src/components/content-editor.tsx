import { useState, useCallback, useRef, useEffect } from 'react';
import { detectContentKind, isBinaryDbType, isLongTextDbType, type ContentKind } from '@/lib/content-detect';
import { cn } from '@/lib/utils';
import { Button } from '@/components/ui/button';
import { Label } from '@/components/ui/label';
import { Binary, WrapText } from 'lucide-react';

interface ContentEditorProps {
    name: string;
    label: string;
    value: string;
    dbType: string;
    onChange: (value: string) => void;
    onBlur: () => void;
    required?: boolean;
    invalid?: boolean;
}

export function ContentEditor({
    name,
    label,
    value,
    dbType,
    onChange,
    onBlur,
    required,
    invalid,
}: ContentEditorProps) {
    const isBinary = isBinaryDbType(dbType);
    const isLong = isLongTextDbType(dbType);
    const kind = detectContentKind(value, dbType);
    const [formatError, setFormatError] = useState<string | null>(null);
    const textareaRef = useRef<HTMLTextAreaElement>(null);

    // Auto-resize textarea
    const adjustHeight = useCallback(() => {
        const el = textareaRef.current;
        if (!el) return;
        el.style.height = 'auto';
        el.style.height = Math.min(el.scrollHeight, 400) + 'px';
    }, []);

    useEffect(adjustHeight, [value, adjustHeight]);

    if (isBinary) {
        return <BinaryViewer name={name} label={label} value={value} />;
    }

    // Short text that isn't structured — use regular input
    if (!isLong && kind === 'text') {
        return null; // Signal caller to use default Input
    }

    const handleFormat = () => {
        setFormatError(null);
        if (kind === 'json') {
            try {
                onChange(JSON.stringify(JSON.parse(value), null, 2));
            } catch (e) {
                setFormatError('Invalid JSON');
            }
        } else if (kind === 'xml') {
            // Simple XML formatting: add newlines after closing tags
            try {
                const formatted = value
                    .replace(/>\s*</g, '>\n<')
                    .replace(/(<[^/][^>]*[^/]>)\n/g, '$1\n')
                    .trim();
                onChange(formatted);
            } catch {
                setFormatError('Could not format XML');
            }
        }
    };

    const handleMinify = () => {
        setFormatError(null);
        if (kind === 'json') {
            try {
                onChange(JSON.stringify(JSON.parse(value)));
            } catch (e) {
                setFormatError('Invalid JSON');
            }
        } else if (kind === 'xml') {
            onChange(value.replace(/\n\s*/g, '').trim());
        }
    };

    const canFormat = kind === 'json' || kind === 'xml';

    return (
        <div className="grid gap-1">
            {canFormat && (
                <div className="flex items-center gap-1 justify-end">
                    <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        className="h-6 text-xs px-2"
                        onClick={handleFormat}
                    >
                        <WrapText className="size-3" />
                        Format
                    </Button>
                    <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        className="h-6 text-xs px-2"
                        onClick={handleMinify}
                    >
                        Minify
                    </Button>
                </div>
            )}
            <textarea
                ref={textareaRef}
                id={name}
                value={value}
                onChange={(e) => { onChange(e.target.value); setFormatError(null); }}
                onBlur={onBlur}
                required={required}
                aria-invalid={invalid}
                className={cn(
                    'flex w-full rounded-md border border-input bg-background px-3 py-2',
                    'text-xs font-mono leading-relaxed',
                    'ring-offset-background focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2',
                    'disabled:cursor-not-allowed disabled:opacity-50',
                    'resize-none min-h-[80px] max-h-[400px]',
                    invalid && 'border-destructive',
                )}
            />
            {formatError && (
                <p className="text-xs text-destructive">{formatError}</p>
            )}
        </div>
    );
}

function BinaryViewer({ name, label, value }: { name: string; label: string; value: string }) {
    const byteLength = value ? Math.ceil(value.length * 3 / 4) : 0;
    const sizeLabel = byteLength < 1024
        ? `${byteLength} bytes`
        : byteLength < 1024 * 1024
            ? `${(byteLength / 1024).toFixed(1)} KB`
            : `${(byteLength / (1024 * 1024)).toFixed(1)} MB`;

    // Show first 256 chars of base64 as hex-like preview
    const preview = value
        ? value.slice(0, 256) + (value.length > 256 ? '...' : '')
        : '(empty)';

    return (
        <div className="grid gap-2">
            <Label htmlFor={name} className="flex items-center gap-1.5">
                <Binary className="size-3.5 text-muted-foreground" />
                {label}
                <span className="text-xs text-muted-foreground font-normal ml-1">
                    (binary, {sizeLabel})
                </span>
            </Label>
            <div className="rounded-md border border-input bg-muted/30 p-3 max-h-[160px] overflow-auto">
                <pre className="text-xs font-mono text-muted-foreground break-all whitespace-pre-wrap">
                    {preview}
                </pre>
            </div>
            <p className="text-xs text-muted-foreground">
                Binary fields are read-only in this editor.
            </p>
        </div>
    );
}
