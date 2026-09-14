import { useState } from 'react'
import { createRoot } from 'react-dom/client'
import ConsentActionCard from '../../src/ConsentActionCard'
import '../../src/index.css'
import '../../src/App.css'

const consentLink =
  'https://logic-apis-eastus2.consent.azure-apim.net/login?data=opaque'

function Harness() {
  const [event, setEvent] = useState('ready')

  return (
    <main style={{ padding: 24 }}>
      <ConsentActionCard
        pending={{
          kind: 'oauth',
          responseId: 'response-id',
          originalMessage: 'profile request',
          consentLink,
          consentCreatedAt: Date.now(),
        }}
        isSending={false}
        onContinue={() => setEvent('continue')}
        onRefresh={() => setEvent('refresh')}
        onCopy={() => setEvent('copy')}
      />
      <output data-testid="event">{event}</output>
    </main>
  )
}

createRoot(document.getElementById('root')!).render(<Harness />)
