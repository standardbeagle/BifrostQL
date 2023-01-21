import React from 'react'
import ReactDOM from 'react-dom';

import { AuthProvider } from "react-oauth2-code-pkce"

import App from './App'
//import './index.css'

const authConfig = {
    clientId: import.meta.env.VITE_CLIENT_ID,
    authorizationEndpoint: import.meta.env.VITE_AUTH_ENDPOINT,
    tokenEndpoint: import.meta.env.VITE_TOKEN_ENDPOINT,
    redirectUri: import.meta.env.VITE_REDIRECT_URI,
    scope: import.meta.env.VITE_SCOPE.split(" "),
    onRefreshTokenExpire: (event) => window.confirm('Session expired. Refresh page to continue using the site?') && event.login(),
    decodeToken: false
}

ReactDOM.render((
    <AuthProvider authConfig={authConfig}>
        <App />
    </AuthProvider>
), document.getElementById('root'));
