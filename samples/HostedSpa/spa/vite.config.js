import { defineConfig } from 'vite';

// Build output goes straight into the ASP.NET host's wwwroot so the sample runs
// with a single `dotnet run`. `dev` proxies /graphql to the host on port 5000.
export default defineConfig({
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/graphql': 'http://localhost:5000',
    },
  },
});
