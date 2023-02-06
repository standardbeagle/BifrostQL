import React from 'react';
import { useReducer, useContext, DispatchWithoutAction, createContext, forwardRef, ReactElement, ReactNode, Children, isValidElement } from 'react';
import { createActions, handleActions } from 'redux-actions';

interface NavContext {
    path: string;
    history: string[];
    location: number;
}
interface PathData {
    path: string;
    data: any;
    hash: string;
    query: string;
}
interface RouteContext extends PathData {
}

interface Action<T> {
    type: string;
    payload: T;
}

const defaultState: NavContext = { path: '/', history: [], location: 0 };
const defaultRoute: RouteContext = { path: '*', data: {}, hash: '', query: '' };

const PathContext = createContext(defaultState);
const PathDispatchContext = createContext<((x: any) => void) | null>(null);
const RouteContext = createContext<RouteContext>(defaultRoute);

const { navigate, back, forward } = createActions({
    NAVIGATE: (path: string) => path,
    BACK: (count: number = 1) => count,
    FORWARD: (count: number = 1) => count,
});

interface PathMatch extends PathData {
    isMatch: boolean,
    remainer: string,
}

const reducer = handleActions({
    NAVIGATE: (state: NavContext, action: Action<any>): NavContext => {
        let { history, location } = state;
        if (location > 0) {
            history = history.slice(location)
        }
        return {
            path: combinePaths(state.path, action.payload),
            history: [action.payload, ...history],
            location: 0,
        }
    },
    BACK: (state: NavContext, action: Action<number>): NavContext => {
        const location = state.location + action.payload;
        const newPath = state.history.at(location);
        if (!newPath)
            return state;
        return {
            path: newPath,
            history: state.history,
            location
        }
    },
    FORWARD: (state: NavContext, action: Action<number>): NavContext => {
        const location = state.location - action.payload;
        const newPath = state.history.at(location);
        if (!newPath)
            return state;
        return {
            path: newPath,
            history: state.history,
            location
        }
    }
},
    defaultState
)

export function usePath(): string {
    const pathState = useContext(PathContext) as NavContext;
    return pathState.path;
}

export function useHistory(): string[] {
    const pathState = useContext(PathContext) as NavContext;
    return pathState.history;
}

export function useNavigate(): (url: string) => void {
    const dispatch = useContext(PathDispatchContext) as (x: any) => void;
    return (path: string) => dispatch(navigate(path));
}

export function useNavigation(): any {
    const dispatch = useContext(PathDispatchContext) as (x: any) => void;
    const pathState = useContext(PathContext) as NavContext;
    return {
        navigate: (path: string) => dispatch(navigate(path)),
        back: (count: number = 1) => dispatch(back(count)),
        forward: (count: number = 1) => dispatch(forward(count)),
        hasBack: pathState.location < pathState.history.length - 1,
        ...pathState
    };
}

export function useParams(): any {
    return useContext(RouteContext).data;
}

export function useSearchParams(): any {
    var { query, hash } = useContext(RouteContext);
    return { search: new URLSearchParams(query), hash };
}

export function useRouteError(): any {
    return {};
}

export function PathProvider({ path, children }: { path: string, children: any }) {
    const initialState = !!path ? { path: path, history: [], location: 0 } : defaultState;
    const [state, dispatch] = useReducer(reducer, initialState) as [NavContext, DispatchWithoutAction];

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

export const Link = forwardRef((props: any, ref: any) => {
    const { to, children, ...rest } = props as { to: string, children: ReactElement[] };
    const navigate = useNavigate();

    const handleClick = (event: React.MouseEvent<HTMLAnchorElement, MouseEvent>) => {
        if (!event.defaultPrevented) event.preventDefault();
        navigate(to);
    }

    return <a {...rest}
        href={to}
        ref={ref as any}
        onClick={handleClick} >
        {children}
    </a>;
});

export function Routes({ children }: { children: ReactNode }) {
    const routeContext = useContext(RouteContext);
    const pathContext = useContext(PathContext);
    const routes = flatRoutes(children, routeContext.path)
        .map(r => [r, matchPath(r.route, pathContext.path)])
        .filter(([r, match]) => match.isMatch);
    return <>{ routes.map(([route, match], index) => {
        return (<RouteContext.Provider key={ index } value={{ ...match }}>{route.element}</RouteContext.Provider>);
    })}</>
}

export function Route({ path, element }: { path: string, element: ReactElement }): ReactElement {
    return <></>;
}

function flatRoutes(children: ReactNode, base: string): any[] {
    const result: any[] = [];
    Children.map(children, (element) => {
        if (!isValidElement(element)) return;

        if (element.type === React.Fragment) {
            result.push.apply(result, flatRoutes(element.props.children, base));
            return;
        }

        result.push({
            route: combineRoutes(base, element.props.path),
            element: element.props.element
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
    const routeSegments = route.split('/');
    let pathSegments = path.split('/');
    if (pathSegments.at(-1) === "")
        pathSegments = pathSegments.slice(0, -1);
    if (routeSegments.length > pathSegments.length)
        return { isMatch: false, remainer: "", data: {}, query: "", hash: "", path: "" };
    if (routeSegments.length < pathSegments.length && routeSegments.at(-1) !== "*")
        return { isMatch: false, remainer: "", data: {}, query: "", hash: "", path: "" };
    let data: any = {};
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

