import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react-swc'
import path from 'node:path';
import dts from 'vite-plugin-dts';
import tailwindcss from '@tailwindcss/vite';

// https://vitejs.dev/config/
export default defineConfig(({ mode }) => ({
  plugins: [tailwindcss(), react(), dts({ insertTypesEntry: true })],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  define: {
    'import.meta.NODE_ENV': '"production"' //Hack for crazy output from react
  },
  build: {
    minify: mode === 'production',
    sourcemap: mode !== 'production',
    lib: {
      entry: path.resolve(__dirname, 'src/index.ts'),
      name: 'editor',
      formats: ['es', 'umd'],
      fileName: (format) => `editor.${format}.${( format ==='umd' ? "c" : "")}js`,
    },
    rollupOptions: {
      // Externalized packages are regular `dependencies` (not peerDependencies) of this
      // package, so any real installer (npm/pnpm) pulls them in transitively for a consumer —
      // the same pattern already relied on for @tanstack/react-query. Verified against both
      // in-repo consumers (examples/host-edit-db, src/BifrostQL.UI/frontend): neither declares
      // these packages directly, but their builds still resolve them, because the bundled dist
      // file lives inside this package's own directory tree, so Node's node_modules walk-up
      // finds them under examples/edit-db/node_modules regardless of the consumer's own deps.
      // Bundling them instead would duplicate ~240KB (radix-ui + lucide-react + react-table +
      // react-form) into every consumer bundle for no benefit.
      external: [
        'react',
        'react-dom',
        '@tanstack/react-query',
        '@tanstack/react-table',
        '@tanstack/react-form',
        'lucide-react',
        'radix-ui',
        '@radix-ui/react-slot',
      ],
      output: {
        exports: 'named',
        globals: {
          react: 'React',
          'react-dom': 'ReactDOM',
          '@tanstack/react-query': 'ReactQuery',
          '@tanstack/react-table': 'ReactTable',
          '@tanstack/react-form': 'ReactForm',
          'lucide-react': 'LucideReact',
          'radix-ui': 'RadixUI',
          '@radix-ui/react-slot': 'RadixReactSlot',
        },
      },
    },
  }
}))
