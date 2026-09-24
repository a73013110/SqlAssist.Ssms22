import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'SqlAssist for SSMS 22',
  lang: 'zh-TW',
  description: '為 SSMS 22.9.x 量身打造的 T-SQL 智慧自動補全與結構預覽擴充套件。',
  base: '/SqlAssist.Ssms22/',
  ignoreDeadLinks: false,
  sitemap: {
    hostname: 'https://a73013110.github.io/SqlAssist.Ssms22/'
  },

  markdown: {
    lineNumbers: true,
    image: { lazyLoading: true }
  },

  head: [
    ['link', { rel: 'icon', type: 'image/x-icon', href: '/SqlAssist.Ssms22/favicon.ico' }],
    ['link', { rel: 'shortcut icon', type: 'image/x-icon', href: '/SqlAssist.Ssms22/favicon.ico' }],
    ['link', { rel: 'apple-touch-icon', href: '/SqlAssist.Ssms22/images/SqlAssist.Icon.512.png' }],
    ['meta', { name: 'author', content: 'a73013110' }],
    ['meta', { name: 'google-site-verification', content: 'NqcYLT7PkECm4jXiq6hiycAP4bKuzxlRj_sVd7de6I0' }],
    ['meta', { name: 'keywords', content: 'SSMS, SQL Server, T-SQL, IntelliSense, Auto-Completion, VSIX, SSMS 22, 結構預覽, 自動補全' }],
    ['meta', { property: 'og:type', content: 'website' }],
    ['meta', { property: 'og:title', content: 'SqlAssist for SSMS 22' }],
    ['meta', { property: 'og:description', content: 'SSMS 22 的 SQL 補全、結構預覽、物件搜尋與查詢收藏擴充套件。' }],
    ['meta', { property: 'og:image', content: 'https://a73013110.github.io/SqlAssist.Ssms22/images/hero.png' }],
    ['meta', { property: 'og:locale', content: 'zh_TW' }],
    ['meta', { name: 'twitter:card', content: 'summary_large_image' }],
    ['meta', { name: 'twitter:title', content: 'SqlAssist for SSMS 22' }],
    ['meta', { name: 'twitter:description', content: '為 SSMS 22.9.x 量身打造的 T-SQL 智慧自動補全與結構預覽外掛。' }],
    ['meta', { name: 'twitter:image', content: 'https://a73013110.github.io/SqlAssist.Ssms22/images/hero.png' }],
    ['meta', { name: 'theme-color', content: '#6a35a5' }]
  ],

  themeConfig: {
    logo: '/images/SqlAssist.Icon.128.png',
    siteTitle: 'SqlAssist',

    nav: [
      { text: '首頁', link: '/' },
      { text: 'English', link: '/en' },
      { text: '操作播放器', link: '/demos/feature-demos.html', target: '_blank' },
      { text: '開始使用', link: '/guide/getting-started' },
      { text: '功能導覽', link: '/#功能展示' },
      { text: '文檔索引', link: '/guide/index' },
      { text: '下載 VSIX', link: 'https://github.com/a73013110/SqlAssist.Ssms22/releases' }
    ],

    sidebar: {
      '/guide/': [
        {
          text: '🚀 入門與架構',
          items: [
            { text: '快速開始', link: '/guide/getting-started' },
            { text: '核心架構', link: '/guide/architecture' },
            { text: '設定選項', link: '/guide/settings' },
            { text: '設定頁入口', link: '/guide/settings-entries' },
            { text: '主題與色彩', link: '/guide/themes' }
          ]
        },
        {
          text: '💡 智慧自動補全',
          items: [
            { text: '自動補全總覽', link: '/guide/completion' },
            { text: '關鍵字建議', link: '/guide/completion-keywords' },
            { text: '關鍵字上下文邊界', link: '/guide/completion-context' },
            { text: '欄位與別名', link: '/guide/completion-columns' },
            { text: '指令碼宣告的資料表', link: '/guide/script-tables' },
            { text: '變數與模組參數', link: '/guide/completion-variables' },
            { text: '限定名稱解析', link: '/guide/qualified-names' },
            { text: '物件種類判定', link: '/guide/completion-object-kinds' },
            { text: '定序建議', link: '/guide/completion-collation' },
            { text: '內建說明與型態', link: '/guide/builtin-help' },
            { text: '參數資訊提示', link: '/guide/parameter-hint' }
          ]
        },
        {
          text: '⚡ 展開、片段與編輯',
          items: [
            { text: '萬用字元 * 展開', link: '/guide/wildcard-expansion' },
            { text: 'INSERT / EXEC 語句展開', link: '/guide/statement-expansion' },
            { text: '展開的欄位與預留值', link: '/guide/statement-values' },
            { text: '自訂函式呼叫插入', link: '/guide/function-call-insertion' },
            { text: 'T-SQL 片段導航', link: '/guide/snippets' },
            { text: '片段 Tab 導航', link: '/guide/snippet-navigation' },
            { text: '片段包夾選取範圍', link: '/guide/snippet-surround' },
            { text: '自動配對括號與引號', link: '/guide/auto-pairing' },
            { text: 'BEGIN/END 區塊配對', link: '/guide/block-matching' }
          ]
        },
        {
          text: '🔍 預覽、格線與定義',
          items: [
            { text: '原地物件結構預覽', link: '/guide/structure-preview' },
            { text: '預覽視窗行為', link: '/guide/preview-window' },
            { text: 'F12 移至定義腳本', link: '/guide/go-to-definition' },
            { text: 'F12 定義指令碼產生', link: '/guide/definition-scripts' },
            { text: '結果格線增強工具', link: '/guide/result-grid' },
            { text: '格線快速腳本產生', link: '/guide/result-grid-generation' },
            { text: '結構健檢分析', link: '/guide/schema-analysis' },
            { text: '殼層命令', link: '/guide/shell-commands' }
          ]
        },
        {
          text: '🔔 通知與中繼資料',
          items: [
            { text: '背景通知提示', link: '/guide/notifications' },
            { text: '通知可見度與降級', link: '/guide/notifications-visibility' },
            { text: '中繼資料分層與快取', link: '/guide/metadata' },
            { text: '跨資料庫中繼資料', link: '/guide/metadata-cross-db' },
            { text: '目前連線與 USE', link: '/guide/metadata-connection' },
            { text: '連結伺服器與遠端', link: '/guide/metadata-remote' },
            { text: '舊版相容與降級', link: '/guide/metadata-compatibility' }
          ]
        },
        {
          text: '搜尋與查詢收藏',
          items: [
            { text: 'SQL Search', link: '/guide/search' },
            { text: 'SQL Memory', link: '/guide/sql-memory' }
          ]
        },
        {
          text: '🛠️ 開發與維護',
          items: [
            { text: '文件路由清單', link: '/guide/index' },
            { text: '指令碼產生風格', link: '/guide/script-generation' },
            { text: '版本發布指引', link: '/guide/release' },
            { text: '網站與動畫發布', link: '/guide/website' }
          ]
        }
      ]
    },

    outline: {
      level: 'deep',
      label: '本頁目錄'
    },

    docFooter: {
      prev: '上一篇',
      next: '下一篇'
    },

    editLink: {
      pattern: ({ relativePath }) => 'https://github.com/a73013110/SqlAssist.Ssms22/edit/master/' + (relativePath === 'index.md' ? 'README.zh-TW.md' : relativePath === 'en.md' ? 'README.md' : relativePath.replace(/^guide\//, 'docs/')),
      text: '在 GitHub 上編輯此頁'
    },

    socialLinks: [
      { icon: 'github', link: 'https://github.com/a73013110/SqlAssist.Ssms22' }
    ],

    search: {
      provider: 'local',
      options: {
        locales: {
          root: {
            translations: {
              button: {
                buttonText: '搜尋文件',
                buttonAriaLabel: '搜尋文件'
              },
              modal: {
                noResultsText: '找不到相關結果',
                resetButtonTitle: '清除搜尋條件',
                footer: {
                  selectText: '選擇',
                  navigateText: '切換',
                  closeText: '關閉'
                }
              }
            }
          }
        }
      }
    },

    footer: {
      message: '以 Apache-2.0 授權條款發布 · 專為 SSMS 22 設計',
      copyright: 'Copyright © 2026 SqlAssist.Ssms22'
    }
  }
})
