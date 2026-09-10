import { mergeConfig } from 'vite'
import base from '../vite.config'

// Local QA workflow preview: use a disposable Quality database on port 5271.
// Authentication still comes from the existing local Hub services.
export default mergeConfig(base, {
  define: { 'import.meta.env.VITE_PROJECT_TRACKER_URL': JSON.stringify('/project-tracker-api') },
  plugins: [{
    name: 'isolated-quality-preview',
    transform(code: string, id: string) {
      if (id.endsWith('/admin/qualityApi.ts')) {
        return code.replace('resolveModuleApplicationUrl(window.location, 5170)', "'http://localhost:5271'")
      }
    },
  }],
  server: { host: 'localhost', port: 5240, strictPort: true },
})
