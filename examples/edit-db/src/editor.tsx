import { useEffect, useState } from 'react'
import { ApolloClient, InMemoryCache, ApolloProvider, NormalizedCacheObject } from '@apollo/client'
import { MainFrame } from './main-frame';
import { PathProvider } from './hooks/usePath';
import { SchemaProvider } from './hooks/useSchema';

interface EditorProps {
    uri?: string;
    uiPath?: string;
    client?: ApolloClient<NormalizedCacheObject>;
    onLocate?: (location: string) => void;
}

const clients: Record<string, ApolloClient<NormalizedCacheObject>> = {};

export function Editor({
    uri,
    client,
    uiPath,
    onLocate,
}: EditorProps) {
    const [uriClient, setUriClient] = useState<ApolloClient<NormalizedCacheObject> | null>(null);


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
            <PathProvider path={uiPath || "/"}>
                <SchemaProvider>
                    <div><MainFrame onLocate={onLocate} /></div>
                </SchemaProvider>
            </PathProvider>
        </ApolloProvider>
    )
}
