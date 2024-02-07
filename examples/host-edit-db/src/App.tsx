import { useState } from 'react'
import './App.css'
import Editor from '@standardbeagle/edit-db'

function App() {

  return (
    <>
      <h1>Host Edit DB</h1>
      <p>Host Edit DB</p>
      <Editor uri='https://localhost:7077/graphql' />
    </>
  )
}

export default App
