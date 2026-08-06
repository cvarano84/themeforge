import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { AuthProvider } from '@/lib/auth'
import { BrandedRoutes } from '@/components/BrandedRoutes'
import './app/globals.css'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter basename={import.meta.env.BASE_URL}>
      <AuthProvider>
        <BrandedRoutes />
      </AuthProvider>
    </BrowserRouter>
  </StrictMode>,
)
