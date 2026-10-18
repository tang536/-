import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import App from './App'
import { UIProvider } from '@/components/UIProvider'
import { initSpringCards } from '@/utils/springCards'
import '@/styles/index.css'

// 卡片弹射归位：document 级事件委托，一次注册全站生效
initSpringCards()

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <BrowserRouter>
      <UIProvider>
        <App />
      </UIProvider>
    </BrowserRouter>
  </React.StrictMode>
)
