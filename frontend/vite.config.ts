import { reactRouter } from "@react-router/dev/vite";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vite";
import tsconfigPaths from "vite-tsconfig-paths";

export default defineConfig(({ isSsrBuild }) => ({
  server: {
    allowedHosts: [".net"],
  },
  build: {
    rollupOptions: isSsrBuild
      ? { input: "./server/app.ts" }
      : {
          // OPTIMIZATION: Code splitting for better initial load performance
          output: {
            manualChunks: {
              // Vendor chunks - separate large libraries
              'react-vendor': ['react', 'react-dom', 'react-router'],
              'bootstrap-vendor': ['bootstrap', 'react-bootstrap'],
              // Utility chunks - group by functionality
              'utils': [
                './app/utils/websocket-util',
                './app/utils/file-size',
                './app/utils/path',
                './app/utils/styling'
              ]
            }
          }
        },
  },
  plugins: [
    tailwindcss(),
    reactRouter(),
    tsconfigPaths()
  ],
}));
