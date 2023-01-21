import { useState, useContext, useEffect } from 'react'
import { AuthContext } from "react-oauth2-code-pkce"
import { createGraphiQLFetcher } from '@graphiql/toolkit';
import { GraphiQL } from 'graphiql';

import './App.css'

const UserInfo = () => {
    const { token, logOut } = useContext(AuthContext)

    return <>
        <button onClick={logOut}>Log Out</button>
    </>
}

const defaultFetcher = createGraphiQLFetcher({
    url: import.meta.env.VITE_GRAPHQL_ENDPOINT,
});


function App() {
    const { token } = useContext(AuthContext);
    const [state, setState] = useState({ fetcher: defaultFetcher});
    console.log("eff", state)
    useEffect(() => {
        if (token) {
            const authFetcher = createGraphiQLFetcher({
                url: import.meta.env.VITE_GRAPHQL_ENDPOINT,
                headers: { Authorization: `bearer ${token}` }
            })
            setState({ fetcher: authFetcher });
            return;
        }
        setState({ fetcher: defaultFetcher });
    }, [token])
    return (
        <div className="app-frame">
            <UserInfo />
            <GraphiQL fetcher={ state.fetcher || defaultFetcher} />
        </div>
    )
}

export default App
