import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react-swc'
//import VitePluginBrowserSync from 'vite-plugin-browser-sync'
import path from 'node:path';
import dts from 'vite-plugin-dts';

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [react(), dts({ insertTypesEntry: true })],
  build: {
    lib: {
      entry: path.resolve(__dirname, 'src/index.ts'),
      name: 'editor',
      // formats: ['es', 'umd'],
      fileName: (format) => `editor.${format}.${( format ==='umd' ? "c" : "")}js`,
    },
    rollupOptions: {
      external: ['react', 'react-dom', "@apollo/client", "graphql"],
      output: {
        globals: {
          react: 'React',
          'react-dom': 'ReactDOM',
        },
      },
    },
  }
})
