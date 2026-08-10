import { useCallback, useRef, useState } from 'react';

/**
 * Persistent, drag-resizable panel dimension for the shell's panes (SQL
 * console editor, saved-query list, form-builder split). Same contract as
 * edit-db's hook of the same name — the two client stacks are deliberately
 * separate (see AGENTS.md "Two Client Stacks"), so this is a parallel
 * implementation, not an import.
 *
 * Returns the size plus pointer/keyboard handlers for a separator element.
 * Sizes clamp to [min, max] and persist per key; keyboard arrows resize in
 * 16px steps so the separator is reachable without a mouse. `invert` is for
 * separators that sit BEFORE the panel they size.
 */
export interface PanelSizeOptions {
  key: string;
  initial: number;
  min: number;
  max: number;
  axis: 'x' | 'y';
  invert?: boolean;
}

const STORAGE_PREFIX = 'bifrost-ui:panel:';
const KEYBOARD_STEP = 16;

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

function loadSize(key: string, initial: number, min: number, max: number): number {
  try {
    const raw = localStorage.getItem(STORAGE_PREFIX + key);
    const parsed = raw === null ? NaN : Number(raw);
    return Number.isFinite(parsed) ? clamp(parsed, min, max) : initial;
  } catch {
    return initial;
  }
}

function saveSize(key: string, value: number): void {
  try {
    localStorage.setItem(STORAGE_PREFIX + key, String(Math.round(value)));
  } catch {
    // Persistence is best-effort; blocked storage must not break resizing.
  }
}

export function usePanelSize({ key, initial, min, max, axis, invert = false }: PanelSizeOptions) {
  const [size, setSize] = useState(() => loadSize(key, initial, min, max));
  const dragState = useRef<{ start: number; startSize: number } | null>(null);

  const apply = useCallback((next: number) => {
    const clamped = clamp(next, min, max);
    setSize(clamped);
    saveSize(key, clamped);
  }, [key, min, max]);

  const onPointerDown = useCallback((e: React.PointerEvent<HTMLElement>) => {
    if (e.button !== 0) return;
    e.preventDefault();
    const target = e.currentTarget;
    target.setPointerCapture(e.pointerId);
    dragState.current = { start: axis === 'x' ? e.clientX : e.clientY, startSize: size };

    const onMove = (ev: PointerEvent) => {
      if (!dragState.current) return;
      const pos = axis === 'x' ? ev.clientX : ev.clientY;
      const delta = (pos - dragState.current.start) * (invert ? -1 : 1);
      apply(dragState.current.startSize + delta);
    };
    const onUp = (ev: PointerEvent) => {
      dragState.current = null;
      try { target.releasePointerCapture(ev.pointerId); } catch { /* already released */ }
      target.removeEventListener('pointermove', onMove);
      target.removeEventListener('pointerup', onUp);
      target.removeEventListener('pointercancel', onUp);
    };
    target.addEventListener('pointermove', onMove);
    target.addEventListener('pointerup', onUp);
    target.addEventListener('pointercancel', onUp);
  }, [apply, axis, invert, size]);

  const onKeyDown = useCallback((e: React.KeyboardEvent<HTMLElement>) => {
    const grow = axis === 'x' ? 'ArrowRight' : 'ArrowDown';
    const shrink = axis === 'x' ? 'ArrowLeft' : 'ArrowUp';
    if (e.key !== grow && e.key !== shrink) return;
    e.preventDefault();
    const direction = (e.key === grow ? 1 : -1) * (invert ? -1 : 1);
    apply(size + direction * KEYBOARD_STEP);
  }, [apply, axis, invert, size]);

  return { size, onPointerDown, onKeyDown };
}
