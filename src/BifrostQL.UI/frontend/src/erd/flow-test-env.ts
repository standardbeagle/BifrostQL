/**
 * jsdom scaffolding for tests that render the REAL @xyflow/react renderer.
 *
 * React Flow only draws an edge once both endpoint nodes report a measured
 * size: `getEdgePosition` returns null while `node.measured` is empty, and the
 * edge wrapper then renders nothing. Measurement comes from a ResizeObserver
 * plus `offsetWidth`/`offsetHeight`, and jsdom supplies neither — so a test
 * that renders the pane without this helper sees the nodes and NONE of the
 * edges, which is exactly the state a test about edge behaviour must not be in.
 */
const NODE_WIDTH = 220;
const NODE_HEIGHT = 90;

export function installFlowMeasurement(): void {
  class ImmediateResizeObserver {
    constructor(private readonly callback: ResizeObserverCallback) {}
    observe(target: Element) {
      const contentRect = { width: NODE_WIDTH, height: NODE_HEIGHT, x: 0, y: 0, top: 0, left: 0, right: NODE_WIDTH, bottom: NODE_HEIGHT };
      this.callback([{ target, contentRect } as unknown as ResizeObserverEntry], this as unknown as ResizeObserver);
    }
    unobserve() {}
    disconnect() {}
  }
  (globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ImmediateResizeObserver;
  (globalThis as unknown as { DOMMatrixReadOnly: unknown }).DOMMatrixReadOnly = class {
    m22 = 1;
    constructor(_transform?: string) {}
  };
  Element.prototype.getBoundingClientRect = function getBoundingClientRect() {
    return { width: NODE_WIDTH, height: NODE_HEIGHT, x: 0, y: 0, top: 0, left: 0, right: NODE_WIDTH, bottom: NODE_HEIGHT, toJSON: () => ({}) } as DOMRect;
  };
  Object.defineProperty(HTMLElement.prototype, 'offsetWidth', { configurable: true, get: () => NODE_WIDTH });
  Object.defineProperty(HTMLElement.prototype, 'offsetHeight', { configurable: true, get: () => NODE_HEIGHT });
}
