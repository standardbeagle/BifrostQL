import { detectContentKind, truncateForCell, formatBinaryPreview, type ContentKind } from '@/lib/content-detect';
import {
    HoverCard,
    HoverCardContent,
    HoverCardTrigger,
} from '@/components/ui/hover-card';
import { Braces, Code, FileText, Binary, Database, Expand } from 'lucide-react';

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
    'php-serialized': 'PHP',
    binary: 'Binary',
    longtext: 'Text',
    text: 'Text',
};

interface ContentViewerProps {
    value: unknown;
    dbType?: string;
    onExpand?: () => void;
}

function formatForPreview(value: string, kind: ContentKind): string {
    if (kind === 'json') {
        try {
            return JSON.stringify(JSON.parse(value), null, 2);
        } catch {
            return value;
        }
    }
    return value;
}

export function ContentViewer({ value, dbType, onExpand }: ContentViewerProps) {
    const str = String(value ?? '');
    if (!str) return null;

    const kind = detectContentKind(str, dbType);

    if (kind === 'text') {
        return <span>{str}</span>;
    }

    if (kind === 'binary') {
        return (
            <span className="inline-flex items-center gap-1.5 text-muted-foreground">
                <Binary className="size-3.5" />
                <span className="text-xs font-mono">{formatBinaryPreview(str)}</span>
                {onExpand && (
                    <button
                        type="button"
                        className="shrink-0 opacity-40 hover:opacity-100 transition-opacity"
                        onClick={(e) => { e.stopPropagation(); onExpand(); }}
                        aria-label="Expand content"
                    >
                        <Expand className="size-3" />
                    </button>
                )}
            </span>
        );
    }

    const Icon = kindIcons[kind];
    const truncated = truncateForCell(str);
    const needsPreview = str.length > 80 || kind === 'json' || kind === 'xml' || kind === 'php-serialized';

    if (!needsPreview) {
        return (
            <span className="inline-flex items-center gap-1.5">
                <Icon className="size-3 text-muted-foreground shrink-0" />
                <span className="font-mono text-xs">{str}</span>
                {onExpand && (
                    <button
                        type="button"
                        className="shrink-0 opacity-40 hover:opacity-100 transition-opacity"
                        onClick={(e) => { e.stopPropagation(); onExpand(); }}
                        aria-label="Expand content"
                    >
                        <Expand className="size-3" />
                    </button>
                )}
            </span>
        );
    }

    return (
        <HoverCard openDelay={300} closeDelay={200}>
            <HoverCardTrigger asChild>
                <button
                    type="button"
                    className="inline-flex items-center gap-1.5 text-left hover:text-primary transition-colors max-w-full"
                >
                    <Icon className="size-3 text-muted-foreground shrink-0" />
                    <span className="font-mono text-xs truncate">{truncated}</span>
                    {onExpand && (
                        <span
                            role="button"
                            tabIndex={0}
                            className="shrink-0 opacity-40 hover:opacity-100 transition-opacity"
                            onClick={(e) => { e.stopPropagation(); e.preventDefault(); onExpand(); }}
                            onKeyDown={(e) => { if (e.key === 'Enter') { e.stopPropagation(); onExpand(); } }}
                            aria-label="Expand content"
                        >
                            <Expand className="size-3" />
                        </span>
                    )}
                </button>
            </HoverCardTrigger>
            <HoverCardContent
                side="bottom"
                align="start"
                className="w-[420px] max-h-[320px] p-0"
            >
                <div className="flex items-center gap-1.5 px-3 py-1.5 border-b border-border bg-muted/30">
                    <Icon className="size-3.5 text-muted-foreground" />
                    <span className="text-xs font-medium text-muted-foreground">{kindLabels[kind]}</span>
                    <span className="text-xs text-muted-foreground ml-auto">{str.length} chars</span>
                </div>
                <pre className="p-3 text-xs font-mono overflow-auto max-h-[280px] whitespace-pre-wrap break-all">
                    {formatForPreview(str, kind)}
                </pre>
            </HoverCardContent>
        </HoverCard>
    );
}
