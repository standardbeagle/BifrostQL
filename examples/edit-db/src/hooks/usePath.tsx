import React from 'react';
import { useReducer, useContext, createContext, forwardRef, ReactElement, ReactNode, Children, isValidElement } from 'react';

interface NavContext {
    path: string;
    history: string[];
    location: number;
}
interface RouteParams {
    [key: string]: string | undefined;
}

interface PathData {
    path: string;
    data: RouteParams;
    hash: string;
    query: string;
}

interface RouteContext extends PathData {
}

interface NavigationState extends NavContext {
    navigate: (path: string) => void;
    back: (count?: number) => void;
    forward: (count?: number) => void;
    hasBack: boolean;
}

interface SearchParamsResult {
    search: URLSearchParams;
    hash: string;
}

interface FlatRoute {
    route: string;
    element: ReactElement;
}

interface LinkProps extends React.AnchorHTMLAttributes<HTMLAnchorElement> {
    to: string;
    children?: ReactNode;
}

interface Action<T> {
    type: string;
    payload: T;
}

const defaultState: NavContext = { path: '/', history: [], location: 0 };
const defaultRoute: RouteContext = { path: '*', data: {}, hash: '', query: '' };

type PathDispatchFn = (action: unknown) => void;

const PathContext = createContext(defaultState);
const PathDispatchContext = createContext<PathDispatchFn | null>(null);
const RouteContext = createContext<RouteContext>(defaultRoute);

// Plain action creators + reducer (previously redux-actions, dropped to remove
// a runtime dependency for a three-action store).
const navigate = (path: string): Action<string> => ({ type: 'NAVIGATE', payload: path });
const back = (count: number = 1): Action<number> => ({ type: 'BACK', payload: count });
const forward = (count: number = 1): Action<number> => ({ type: 'FORWARD', payload: count });

interface PathMatch extends PathData {
    isMatch: boolean,
    remainer: string,
}

// Cap retained history so a long browsing session can't grow the array without
// bound. 100 entries is far more than the back/forward UI ever surfaces.
const MAX_HISTORY = 100;

function reducer(state: NavContext, action: Action<string> | Action<number>): NavContext {
    switch (action.type) {
        case 'NAVIGATE': {
            let { history, location } = state;
            if (location > 0) {
                history = history.slice(location);
            }
            return {
                path: combinePaths(state.path, action.payload as string),
                history: [action.payload as string, ...history].slice(0, MAX_HISTORY),
                location: 0,
            };
        }
        case 'BACK': {
            const location = state.location + (action.payload as number);
            const newPath = state.history.at(location);
            if (!newPath) return state;
            return { path: newPath, history: state.history, location };
        }
        case 'FORWARD': {
            const location = state.location - (action.payload as number);
            const newPath = state.history.at(location);
            if (!newPath) return state;
            return { path: newPath, history: state.history, location };
        }
        default:
            return state;
    }
}

export function usePath(): string {
    const pathState = useContext(PathContext) as NavContext;
    return pathState.path;
}

export function useHistory(): string[] {
    const pathState = useContext(PathContext) as NavContext;
    return pathState.history;
}

export function useNavigate(): (url: string) => void {
    const dispatch = useContext(PathDispatchContext);
    return (path: string) => dispatch?.(navigate(path));
}

export function useNavigation(): NavigationState {
    const dispatch = useContext(PathDispatchContext);
    const pathState = useContext(PathContext);
    return {
        navigate: (path: string) => dispatch?.(navigate(path)),
        back: (count: number = 1) => dispatch?.(back(count)),
        forward: (count: number = 1) => dispatch?.(forward(count)),
        hasBack: pathState.location < pathState.history.length - 1,
        ...pathState
    };
}

export function useParams<T extends RouteParams = RouteParams>(): T {
    return useContext(RouteContext).data as T;
}

export function useSearchParams(): SearchParamsResult {
    const { query, hash } = useContext(RouteContext);
    return { search: new URLSearchParams(query), hash };
}

export function PathProvider({ path, children }: { path: string, children: ReactNode }) {
    const initialState = !!path ? { path: path, history: [], location: 0 } : defaultState;
    const [state, dispatch] = useReducer(reducer as React.Reducer<NavContext, unknown>, initialState);

    return (
        <PathContext.Provider value={state}>
            <PathDispatchContext.Provider value={dispatch}>
                <RouteContext.Provider value={defaultRoute}>
                    {children}
                </RouteContext.Provider>
            </PathDispatchContext.Provider>
        </PathContext.Provider>
    );
}

export const Link = forwardRef<HTMLAnchorElement, LinkProps>(({ to, children, ...rest }, ref) => {
    const navigate = useNavigate();

    const handleClick = (event: React.MouseEvent<HTMLAnchorElement, MouseEvent>) => {
        if (!event.defaultPrevented) event.preventDefault();
        navigate(to);
    }

    return <a {...rest}
        href={to}
        ref={ref}
        onClick={handleClick} >
        {children}
    </a>;
});

export function Routes({ children }: { children: ReactNode }) {
    const routeContext = useContext(RouteContext);
    const pathContext = useContext(PathContext);
    const flat = flatRoutes(children, routeContext.path);
    const best = selectRoute(flat.map(f => f.route), pathContext.path);
    if (!best) return <></>;
    const element = flat.find(f => f.route === best.route)!.element;
    return <RouteContext.Provider value={{ ...best.match }}>{element}</RouteContext.Provider>;
}

/** Higher = more specific: literal segments rank above `:param`, which ranks above `*`. */
export function routeSpecificity(route: string): number {
    let score = 0;
    for (const seg of route.split('/')) {
        if (seg === '' || seg === '*') continue;
        score += seg.startsWith(':') ? 1 : 100;
    }
    return score;
}

/**
 * Picks the single most-specific matching route for a path. When several
 * same-length routes match (a literal segment vs a `:param` at the same
 * position, e.g. `/:table/edit` vs `/:table/:id` for "/users/edit"), the
 * literal route wins so a route keyword like "edit" is never captured as an
 * `:id` and fired off as a bogus get-by-id ($id="edit"). Ties keep declaration
 * order.
 */
export function selectRoute(routePaths: string[], path: string): { route: string; match: PathMatch } | null {
    let best: { route: string; match: PathMatch } | null = null;
    for (const route of routePaths) {
        const match = matchPath(route, path);
        if (!match.isMatch) continue;
        if (best === null || routeSpecificity(route) > routeSpecificity(best.route))
            best = { route, match };
    }
    return best;
}

export function Route({ path, element }: { path: string, element: ReactElement }): ReactElement {
    return <></>;
}

function flatRoutes(children: ReactNode, base: string): FlatRoute[] {
    const result: FlatRoute[] = [];
    Children.map(children, (element) => {
        if (!isValidElement(element)) return;

        if (element.type === React.Fragment) {
            result.push.apply(result, flatRoutes(element.props.children, base));
            return;
        }

        const props = element.props as { path: string; element: ReactElement };
        result.push({
            route: combineRoutes(base, props.path),
            element: props.element
        });
        return element;
    });
    return result;
}

const combinePaths = (current: string, newPath: string): string => {
    if (newPath.startsWith('/')) return newPath;
    if (newPath.startsWith('?')) {
        const [currentBase] = current?.split('?') ?? [""];
        return currentBase + newPath;
    }
    let up = 0;
    const segments = newPath.split('/').filter(s => s !== "");
    while (segments.length > 0 && (segments[0] === '.' || segments[0] === '..')) {
        const segment = segments.shift();
        if (segment == '..') ++up;
    }
    return mergePaths(current, segments.join('/'), up);
}

const mergePaths = (current: string, newPath: string, ups: number): string => {
    const currentSegments = current.split('/').filter(s => s !== "");
    const newSegments = newPath.split('/').filter(s => s !== "");
    if (ups === 0)
        return `/${currentSegments.join('/')}/${newSegments.join('/')}`;
    return `/${currentSegments.slice(0, -ups).join('/')}/${newSegments.join('/')}`;
}

const combineRoutes = (base: string, child: string): string => {
    const baseSegments = base.split('/').filter(s => s !== "");
    const childSegments = child.split('/').filter(s => s !== "");
    if (baseSegments.length > 0 && baseSegments.at(-1) === "*") baseSegments.pop();
    if (baseSegments.length === 0)
        return `/${childSegments.join('/')}`;
    return `/${baseSegments.join('/')}/${childSegments.join('/')}`;
}

const matchPath = (route: string, path: string): PathMatch => {
    let routeSegments = route.split('/');
    let pathSegments = path.split('/');
    if (routeSegments.at(-1) === "")
        routeSegments = routeSegments.slice(0, -1);
    if (pathSegments.at(-1) === "")
        pathSegments = pathSegments.slice(0, -1);
    if (routeSegments.length > pathSegments.length)
        return { isMatch: false, remainer: "", data: {}, query: "", hash: "", path: "" };
    if (routeSegments.length < pathSegments.length && routeSegments.at(-1) !== "*")
        return { isMatch: false, remainer: "", data: {}, query: "", hash: "", path: "" };
    let data: RouteParams = {};
    let query: string = "";
    let hash: string = "";
    for (let i = 0; i < routeSegments.length; ++i) {
        const routeSegment = routeSegments[i];
        const pathSegment = pathSegments[i];
        const sections = [...pathSegment.matchAll(/([?#]?)((?:(?![?#]).)*)/g)];
        const pathSection = sections?.find(s => s[1] === "")?.[2] ?? "";
        query = sections?.find(s => s[1] === "?")?.[2] ?? "";
        hash = sections?.find(s => s[1] === "#")?.[2] ?? "";
        if (routeSegment.startsWith(':') && pathSection && pathSection !== "") {
            data[routeSegment.substring(1)] = pathSection;
            continue;
        }
        if (routeSegment === "*") {
            return { isMatch: true, data, remainer: pathSegments.splice(0, i).join('/'), query, hash, path: pathSegments.slice(0, i).join('/') };
        }
        if (routeSegment !== pathSection)
            return { isMatch: false, data: {}, remainer: "", query: "", hash: "", path: "" };
    }

    return { isMatch: true, remainer: "", data, query, hash, path: path?.split("?")?.[0] ?? "" };
}

