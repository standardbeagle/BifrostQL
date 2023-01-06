import React, { useEffect, useState } from 'react'
import { ApolloClient, InMemoryCache, ApolloProvider } from '@apollo/client'
import { MainFrame } from './main-frame';
import { PathContext, PathProvider } from './hooks/usePath';

interface EditorProps {
    uri?: string;
    client?: ApolloClient<any>;
    onLocate?: (location: string) => void;
}

let clients: { [name: string]: ApolloClient<any> } = {};

export function Editor({
    uri,
    client,
    onLocate,
}: EditorProps) {
    let [uriClient, setUriClient] = useState<ApolloClient<any> | null>(null);


    useEffect(() => {
        if (client) {
            setUriClient(null);
        }
        if (!uri) return;
        if (!clients[uri]) {
            clients[uri] = new ApolloClient({
                uri: uri,
                cache: new InMemoryCache(),
            });
        }
        setUriClient(clients[uri]);
    }, [uri, client])

    if (!uri && !client) return <section>CONFIG MISSING...</section>
    if (!uriClient && !client) return <section>INITALIZING...</section>

    return (
        <ApolloProvider client={client || uriClient!}>
            <PathProvider path="/">
                <div><MainFrame onLocate={onLocate} /></div>
            </PathProvider>
        </ApolloProvider>
    )
}
