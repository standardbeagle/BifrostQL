import { useEffect, useState } from 'react';
import { resolveMediaUrl, type MediaItem } from '../lib/chat-api';

/**
 * Inline image grid for one SSE `media` event. Stored URLs render directly;
 * binary `bifrost-media://` references are fetched through the auth-gated
 * media route with credentials (the server re-authorizes the row on every
 * fetch) and rendered from an object URL that is revoked on unmount.
 */
export function MediaGrid({ toolName, items }: { toolName: string; items: MediaItem[] }) {
  return (
    <div className="media-grid" aria-label={`Media from ${toolName}`}>
      {items.map((item) => (
        <MediaThumb key={`${item.mediaReference}`} item={item} />
      ))}
    </div>
  );
}

function MediaThumb({ item }: { item: MediaItem }) {
  const [url, setUrl] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let objectUrl: string | null = null;
    let cancelled = false;
    resolveMediaUrl(item.mediaReference)
      .then((resolved) => {
        if (cancelled) {
          if (resolved !== item.mediaReference) URL.revokeObjectURL(resolved);
          return;
        }
        if (resolved !== item.mediaReference) objectUrl = resolved;
        setUrl(resolved);
      })
      .catch((cause: unknown) => {
        if (!cancelled) setError(cause instanceof Error ? cause.message : String(cause));
      });
    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [item.mediaReference]);

  const alt = item.caption ?? `media ${item.id}`;
  if (error) {
    return (
      <figure className="media-thumb failed">
        <div className="media-error">Could not load media: {error}</div>
        <figcaption>{alt}</figcaption>
      </figure>
    );
  }
  return (
    <figure className="media-thumb">
      {url ? <img src={url} alt={alt} /> : <div className="media-loading">Loading…</div>}
      {item.caption && <figcaption>{item.caption}</figcaption>}
    </figure>
  );
}
