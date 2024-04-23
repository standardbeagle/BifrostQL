// vite.config.ts
import { defineConfig } from "file:///C:/Users/andyb/OneDrive/Documents/Work/BifrostQL/examples/edit-db/node_modules/vite/dist/node/index.js";
import react from "file:///C:/Users/andyb/OneDrive/Documents/Work/BifrostQL/node_modules/@vitejs/plugin-react-swc/index.mjs";
import path from "node:path";
import dts from "file:///C:/Users/andyb/OneDrive/Documents/Work/BifrostQL/node_modules/vite-plugin-dts/dist/index.mjs";
var __vite_injected_original_dirname = "C:\\Users\\andyb\\OneDrive\\Documents\\Work\\BifrostQL\\examples\\edit-db";
var vite_config_default = defineConfig({
  plugins: [react(), dts({ insertTypesEntry: true })],
  build: {
    lib: {
      entry: path.resolve(__vite_injected_original_dirname, "src/index.ts"),
      name: "editor",
      formats: ["es", "umd"],
      fileName: (format) => `editor.${format}.${format === "umd" ? "c" : ""}js`
    },
    rollupOptions: {
      external: ["react", "react-dom"],
      output: {
        globals: {
          react: "React",
          "react-dom": "ReactDOM"
        }
      }
    }
  }
});
export {
  vite_config_default as default
};
//# sourceMappingURL=data:application/json;base64,ewogICJ2ZXJzaW9uIjogMywKICAic291cmNlcyI6IFsidml0ZS5jb25maWcudHMiXSwKICAic291cmNlc0NvbnRlbnQiOiBbImNvbnN0IF9fdml0ZV9pbmplY3RlZF9vcmlnaW5hbF9kaXJuYW1lID0gXCJDOlxcXFxVc2Vyc1xcXFxhbmR5YlxcXFxPbmVEcml2ZVxcXFxEb2N1bWVudHNcXFxcV29ya1xcXFxCaWZyb3N0UUxcXFxcZXhhbXBsZXNcXFxcZWRpdC1kYlwiO2NvbnN0IF9fdml0ZV9pbmplY3RlZF9vcmlnaW5hbF9maWxlbmFtZSA9IFwiQzpcXFxcVXNlcnNcXFxcYW5keWJcXFxcT25lRHJpdmVcXFxcRG9jdW1lbnRzXFxcXFdvcmtcXFxcQmlmcm9zdFFMXFxcXGV4YW1wbGVzXFxcXGVkaXQtZGJcXFxcdml0ZS5jb25maWcudHNcIjtjb25zdCBfX3ZpdGVfaW5qZWN0ZWRfb3JpZ2luYWxfaW1wb3J0X21ldGFfdXJsID0gXCJmaWxlOi8vL0M6L1VzZXJzL2FuZHliL09uZURyaXZlL0RvY3VtZW50cy9Xb3JrL0JpZnJvc3RRTC9leGFtcGxlcy9lZGl0LWRiL3ZpdGUuY29uZmlnLnRzXCI7aW1wb3J0IHsgZGVmaW5lQ29uZmlnIH0gZnJvbSAndml0ZSdcbmltcG9ydCByZWFjdCBmcm9tICdAdml0ZWpzL3BsdWdpbi1yZWFjdC1zd2MnXG4vL2ltcG9ydCBWaXRlUGx1Z2luQnJvd3NlclN5bmMgZnJvbSAndml0ZS1wbHVnaW4tYnJvd3Nlci1zeW5jJ1xuaW1wb3J0IHBhdGggZnJvbSAnbm9kZTpwYXRoJztcbmltcG9ydCBkdHMgZnJvbSAndml0ZS1wbHVnaW4tZHRzJztcblxuLy8gaHR0cHM6Ly92aXRlanMuZGV2L2NvbmZpZy9cbmV4cG9ydCBkZWZhdWx0IGRlZmluZUNvbmZpZyh7XG4gIHBsdWdpbnM6IFtyZWFjdCgpLCBkdHMoeyBpbnNlcnRUeXBlc0VudHJ5OiB0cnVlIH0pXSxcbiAgYnVpbGQ6IHtcbiAgICBsaWI6IHtcbiAgICAgIGVudHJ5OiBwYXRoLnJlc29sdmUoX19kaXJuYW1lLCAnc3JjL2luZGV4LnRzJyksXG4gICAgICBuYW1lOiAnZWRpdG9yJyxcbiAgICAgIGZvcm1hdHM6IFsnZXMnLCAndW1kJ10sXG4gICAgICBmaWxlTmFtZTogKGZvcm1hdCkgPT4gYGVkaXRvci4ke2Zvcm1hdH0uJHsoIGZvcm1hdCA9PT0ndW1kJyA/IFwiY1wiIDogXCJcIil9anNgLFxuICAgIH0sXG4gICAgcm9sbHVwT3B0aW9uczoge1xuICAgICAgZXh0ZXJuYWw6IFsncmVhY3QnLCAncmVhY3QtZG9tJ10sXG4gICAgICBvdXRwdXQ6IHtcbiAgICAgICAgZ2xvYmFsczoge1xuICAgICAgICAgIHJlYWN0OiAnUmVhY3QnLFxuICAgICAgICAgICdyZWFjdC1kb20nOiAnUmVhY3RET00nLFxuICAgICAgICB9LFxuICAgICAgfSxcbiAgICB9LFxuICB9XG59KVxuIl0sCiAgIm1hcHBpbmdzIjogIjtBQUF1WSxTQUFTLG9CQUFvQjtBQUNwYSxPQUFPLFdBQVc7QUFFbEIsT0FBTyxVQUFVO0FBQ2pCLE9BQU8sU0FBUztBQUpoQixJQUFNLG1DQUFtQztBQU96QyxJQUFPLHNCQUFRLGFBQWE7QUFBQSxFQUMxQixTQUFTLENBQUMsTUFBTSxHQUFHLElBQUksRUFBRSxrQkFBa0IsS0FBSyxDQUFDLENBQUM7QUFBQSxFQUNsRCxPQUFPO0FBQUEsSUFDTCxLQUFLO0FBQUEsTUFDSCxPQUFPLEtBQUssUUFBUSxrQ0FBVyxjQUFjO0FBQUEsTUFDN0MsTUFBTTtBQUFBLE1BQ04sU0FBUyxDQUFDLE1BQU0sS0FBSztBQUFBLE1BQ3JCLFVBQVUsQ0FBQyxXQUFXLFVBQVUsTUFBTSxJQUFNLFdBQVUsUUFBUSxNQUFNLEVBQUc7QUFBQSxJQUN6RTtBQUFBLElBQ0EsZUFBZTtBQUFBLE1BQ2IsVUFBVSxDQUFDLFNBQVMsV0FBVztBQUFBLE1BQy9CLFFBQVE7QUFBQSxRQUNOLFNBQVM7QUFBQSxVQUNQLE9BQU87QUFBQSxVQUNQLGFBQWE7QUFBQSxRQUNmO0FBQUEsTUFDRjtBQUFBLElBQ0Y7QUFBQSxFQUNGO0FBQ0YsQ0FBQzsiLAogICJuYW1lcyI6IFtdCn0K
