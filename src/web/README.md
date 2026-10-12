# AskDocs web (Angular 19)

Standalone component + signals, no UI library. Builds straight into `../AskDocs.Api/wwwroot`, so the API serves the UI.

```bash
npm install
npm start         # http://localhost:4200, proxies /api to http://localhost:5200
npm run test:ci   # SSE parser + citation splitting tests
```
