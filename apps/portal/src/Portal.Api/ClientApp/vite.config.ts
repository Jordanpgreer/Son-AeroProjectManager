import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': 'http://localhost:5140',
      '/project-tracker-api': {
        target: 'http://localhost:5135',
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/project-tracker-api/, ''),
      },
    },
  },
})
