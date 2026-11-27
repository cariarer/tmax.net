import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  // Relative asset URLs so the bundle works when served by Kestrel from wwwroot.
  base: './',
  server: {
    port: Number(process.env.TMAX_VITE_PORT) || 5995,
    strictPort: true,
  },
  build: { outDir: 'dist', emptyOutDir: true, chunkSizeWarningLimit: 4096 },
});
