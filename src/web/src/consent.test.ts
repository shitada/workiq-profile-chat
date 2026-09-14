import { describe, expect, it } from 'vitest'
import { isAllowedConsentUrl } from './consent'

describe('Work IQ consent URL validation', () => {
  it('allows Microsoft consent endpoints', () => {
    expect(
      isAllowedConsentUrl(
        'https://logic-apis-eastus2.consent.azure-apim.net/login?data=opaque',
      ),
    ).toBe(true)
  })

  it('rejects non-HTTPS and unrelated hosts', () => {
    expect(
      isAllowedConsentUrl(
        'http://logic-apis-eastus2.consent.azure-apim.net/login?data=opaque',
      ),
    ).toBe(false)
    expect(
      isAllowedConsentUrl('https://example.com/login?data=opaque'),
    ).toBe(false)
  })
})
