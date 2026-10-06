import { useState } from 'react'
import { createRoot } from 'react-dom/client'
import ReportWorkspace from '../../src/ReportWorkspace'
import { createReportApi } from '../../src/api'
import { sessionKey } from '../../src/session'
import '../../src/index.css'
import '../../src/App.css'

const api = createReportApi('/test-api', async () => 'test-dummy-access-token')
function Harness() {
  const [account, setAccount] = useState('account-a')
  return <>
    <button onClick={() => setAccount('account-b')}>テスト用ユーザー切替</button>
    <ReportWorkspace key={account} identity={sessionKey('test-tenant', account, 'graph')} provider="graph"
      accountName={account} api={api} onSignOut={() => setAccount('signed-out')} />
  </>
}
createRoot(document.getElementById('root')!).render(<Harness />)
