import { defineConfig, mergeConfig } from 'vite'
import base from '../vite.config'

// Optional development preview against a separately started, disposable Estimating database.
export default mergeConfig(base, defineConfig({
  server: {
    host: 'localhost', port: 5242, strictPort: true,
    proxy: { '/api': { target: 'http://localhost:5282', changeOrigin: true } },
  },
}))
