import React, { useState, useEffect } from 'react'
import { Playground } from 'graphql-playground-react'
import { useAuth } from './auth/useAuth'

const GqlPlayground = props => {
    const { editHeaders } = props

    const auth = useAuth()
    const [token, setToken] = useState()
    const [headers, setHeaders] = useState(props.headers)
    const [loading, setLoading] = useState(true)
    useEffect(() => {
        if (!auth.loading) {
            auth.getAccessToken().then(setToken)
        }
    }, [auth])
    useEffect(() => {
        if (token) {
            setHeaders({
                authorization: `Bearer ${token}`
            })
        }
    }, [token])
    useEffect(() => {
        if (headers) {
            // this is where the magic happens
            editHeaders(JSON.stringify(headers, null, 2))
            setTimeout(() => {
                setLoading(false)
            }, 3000)
        }
    }, [headers])
    return (
        <>
            {loading && (
                <div className="spinner-wrapper">
                    <div className="spinner-node" />
                </div>
            )}
            <Playground endpoint={process.env.SERVICE_URL} />
        </>
    )
}

export const GqlPlayground;