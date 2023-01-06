import React from 'react';
import { useReducer, useContext, DispatchWithoutAction, createContext, forwardRef, ReactElement, ReactNode, Children, isValidElement } from 'react';
import { createActions, handleAction } from 'redux-actions';

interface NavContext {
    path: string;
}
interface RouteContext {
    path: string;
    matches: any;
    hash: string;
    query: string;
}

interface Action<T> {
    type: string;
    payload: T;
}

const defaultState: NavContext = { path: '/' };
const defaultRoute: RouteContext = { path: '*', matches: {}, hash:'', query: '' };

const PathContext = createContext(defaultState);
const PathDispatchContext = createContext<((x: any) => void) | null>(null);
const RouteContext = createContext<RouteContext>(defaultRoute);

const { navigate } = createActions({
    NAVIGATE: (path: string) => path
});

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
    return `/${currentSegments.slice(-ups).join('/')}/${newSegments.join('/')}`;
}

const combineRoutes = (base: string, child: string): string => {
    const baseSegments = base.split('/').filter(s => s !== "");
    const childSegments = child.split('/').filter(s => s !== "");
    if (baseSegments.length > 0 && baseSegments.at(-1) === "*") baseSegments.pop();
    if (baseSegments.length === 0)
        return `/${childSegments.join('/')}`;
    return `/${baseSegments.join('/')}/${childSegments.join('/')}`;
}

interface PathMatch {
    isMatch: boolean,
    path: string,
    query: string,
    hash: string,
    remainer: string,
    data: any
}

const matchPath = (route: string, path: string): PathMatch => {
    const routeSegments = route.split('/');
    const pathSegments = path.split('/');
    if (routeSegments.length > pathSegments.length)
        return { isMatch: false, remainer: "", data: {}, query:"", hash: "", path:"" };
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
            return { isMatch: true, data, remainer: pathSegments.splice(0, i).join('/'), query, hash, path:pathSegments.slice(0, i).join('/') };
        }
        if (routeSegment !== pathSection)
            return { isMatch: false, data: {}, remainer: "", query:"", hash: "", path:""  };
    }

    return { isMatch: true, remainer: "", data, query, hash, path: path?.split("?")?.[0] ?? "" };
}

const reducer = handleAction(navigate,
    (state: NavContext, action: Action<string>) => {
        return { path: combinePaths(state.path, action.payload) }
    },
    defaultState
)

export function usePath(): string {
    const pathState = useContext(PathContext) as NavContext;
    return pathState.path;
}

export function useNavigate(): (url: string) => void {
    const dispatch = useContext(PathDispatchContext) as (x: any) => void;
    return (path: string) => dispatch(navigate(path));
}

export function useParams(): any {
    return useContext(RouteContext).matches;
}

export function useSearchParams(): any {
    var {query, hash} = useContext(RouteContext);
    return { search: new URLSearchParams(query), hash};
}

export function useRouteError(): any {
    return {};
}

export function PathProvider({ path, children }: { path: string, children: any }) {
    const initialState = !!path ? { path: path } : defaultState;
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
    const route = flatRoutes(children, routeContext.path)
        .find(r => matchPath(r.route, pathContext.path).isMatch);
    const match = matchPath(route.route, pathContext.path);
    return (<RouteContext.Provider value={{ path: match.path, matches: match.data, query: match.query, hash: match.hash  }}>
        {route.element}
    </RouteContext.Provider>);
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

